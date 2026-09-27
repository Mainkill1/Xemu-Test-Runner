/* Real-time media only. The runner owns target lifetime, authorization, input and evidence.
 * SPDX-License-Identifier: MIT
 * Copyright (c) 2026 Xemu Test Runner contributors
 */
#include <gst/gst.h>
#include <gst/sdp/sdp.h>
#include <gst/webrtc/webrtc.h>
#include <json-glib/json-glib.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <stdint.h>
#ifdef _WIN32
#include <windows.h>
#include <io.h>
#else
#include <unistd.h>
#include <X11/Xlib.h>
#include <X11/Xatom.h>
#endif

#define LINE_LIMIT (1024 * 1024)
#define DATA_LIMIT 4096
static GMainLoop *loop;
static GstElement *pipeline, *peer;
static GstWebRTCDataChannel *control_channel, *state_channel;
static GMutex output_lock, channel_lock;
static gint failed, stopping, offer_received;
static gboolean screenshot_mode;
static gint64 raw_count, encoded_count;
static GMutex count_lock;

static void emit(JsonBuilder *builder) {
    JsonNode *root = json_builder_get_root(builder);
    JsonGenerator *generator = json_generator_new();
    json_generator_set_root(generator, root);
    gchar *text = json_generator_to_data(generator, NULL);
    g_mutex_lock(&output_lock);
    if (fputs(text, stdout) < 0 || fputc('\n', stdout) == EOF || fflush(stdout) != 0) {
        g_atomic_int_set(&failed, 1);
        if (loop) g_main_loop_quit(loop);
    }
    g_mutex_unlock(&output_lock);
    g_free(text); json_node_free(root); g_object_unref(generator); g_object_unref(builder);
}
static JsonBuilder *message(const char *type) {
    JsonBuilder *b = json_builder_new(); json_builder_begin_object(b);
    json_builder_set_member_name(b, "type"); json_builder_add_string_value(b, type); return b;
}
static void string_field(JsonBuilder *b, const char *key, const char *value) {
    json_builder_set_member_name(b, key); json_builder_add_string_value(b, value ? value : "");
}
static void integer_field(JsonBuilder *b, const char *key, gint64 value) {
    json_builder_set_member_name(b, key); json_builder_add_int_value(b, value);
}
static void finish(JsonBuilder *b) { json_builder_end_object(b); emit(b); }
static void fail(const char *detail) {
    if (!g_atomic_int_compare_and_exchange(&failed, 0, 1)) return;
    JsonBuilder *b = message("error"); string_field(b, "error", detail); finish(b);
    if (loop) g_main_loop_quit(loop);
}
static gboolean bus_message(GstBus *bus, GstMessage *msg, gpointer data) {
    (void)bus; (void)data;
    if (GST_MESSAGE_TYPE(msg) == GST_MESSAGE_ERROR) {
        GError *error = NULL; gchar *debug = NULL; gst_message_parse_error(msg, &error, &debug);
        fail(error ? error->message : "GStreamer error"); g_clear_error(&error); g_free(debug);
    } else if (GST_MESSAGE_TYPE(msg) == GST_MESSAGE_EOS && !g_atomic_int_get(&stopping)) {
        if (screenshot_mode) { g_atomic_int_set(&stopping, 1); g_main_loop_quit(loop); }
        else fail("Capture source ended unexpectedly.");
    }
    return G_SOURCE_CONTINUE;
}
static void ice_candidate(GstElement *element, guint index, gchar *candidate, gpointer data) {
    (void)element; (void)data;
    if (g_atomic_int_get(&stopping)) return;
    JsonBuilder *b = message("ice"); integer_field(b, "sdpMLineIndex", index); string_field(b, "candidate", candidate); finish(b);
}
static gboolean promise_ok(GstPromise *promise) {
    if (gst_promise_wait(promise) != GST_PROMISE_RESULT_REPLIED) return FALSE;
    const GstStructure *reply = gst_promise_get_reply(promise);
    if (reply && gst_structure_has_field(reply, "error")) return FALSE;
    return TRUE;
}
static void local_set(GstPromise *promise, gpointer user_data) {
    GstWebRTCSessionDescription *answer = user_data;
    if (!promise_ok(promise)) fail("WebRTC set-local-description failed.");
    else {
        gchar *sdp = gst_sdp_message_as_text(answer->sdp);
        JsonBuilder *b = message("answer"); string_field(b, "sdp", sdp); finish(b); g_free(sdp);
    }
    gst_webrtc_session_description_free(answer); gst_promise_unref(promise);
}
static void answer_created(GstPromise *promise, gpointer unused) {
    (void)unused; GstWebRTCSessionDescription *answer = NULL;
    if (!promise_ok(promise) || !gst_structure_get(gst_promise_get_reply(promise), "answer", GST_TYPE_WEBRTC_SESSION_DESCRIPTION, &answer, NULL) || !answer) {
        fail("WebRTC answer creation failed."); gst_promise_unref(promise); return;
    }
    gst_promise_unref(promise);
    GstPromise *next = gst_promise_new_with_change_func(local_set, answer, NULL);
    g_signal_emit_by_name(peer, "set-local-description", answer, next);
}
static void remote_set(GstPromise *promise, gpointer unused) {
    (void)unused;
    if (!promise_ok(promise)) { fail("WebRTC set-remote-description failed."); gst_promise_unref(promise); return; }
    gst_promise_unref(promise);
    GstPromise *next = gst_promise_new_with_change_func(answer_created, NULL, NULL);
    g_signal_emit_by_name(peer, "create-answer", NULL, next);
}
static void channel_message(GstWebRTCDataChannel *channel, gchar *text, gpointer unused) {
    (void)unused;
    if (g_atomic_int_get(&stopping)) return;
    if (!text || strlen(text) > DATA_LIMIT || !g_utf8_validate(text, -1, NULL)) { fail("Invalid/oversized controller data."); return; }
    gchar *label = NULL; g_object_get(channel, "label", &label, NULL);
    JsonBuilder *b = message("data"); string_field(b, "channel", label); string_field(b, "data", text); finish(b); g_free(label);
}
static void channel_open(GstWebRTCDataChannel *channel, gpointer unused) {
    (void)unused; gchar *label = NULL; g_object_get(channel, "label", &label, NULL);
    JsonBuilder *b = message("channel-open"); string_field(b, "channel", label); finish(b); g_free(label);
}
static void channel_closed(GstWebRTCDataChannel *channel, gpointer unused) {
    (void)unused; (void)channel;
    if (!g_atomic_int_get(&stopping)) fail("WebRTC data channel closed.");
}
static void prepare_channel(GstElement *element, GstWebRTCDataChannel *channel, gboolean local, gpointer unused) {
    (void)element; (void)local; (void)unused;
    gchar *label = NULL; gboolean ordered = FALSE; gint retransmits = -1, lifetime = -1;
    g_object_get(channel, "label", &label, "ordered", &ordered, "max-retransmits", &retransmits, "max-packet-lifetime", &lifetime, NULL);
    gboolean valid = FALSE;
    g_mutex_lock(&channel_lock);
    if (g_strcmp0(label, "session-control") == 0 && ordered && retransmits == -1 && lifetime == -1 && !control_channel) {
        control_channel = g_object_ref(channel); valid = TRUE;
    } else if (g_strcmp0(label, "input-state") == 0 && !ordered && retransmits == 0 && !state_channel) {
        state_channel = g_object_ref(channel); valid = TRUE;
    }
    g_mutex_unlock(&channel_lock);
    if (!valid) { gst_webrtc_data_channel_close(channel); fail("Unexpected or incorrectly configured data channel."); }
    else {
        g_signal_connect(channel, "on-message-string", G_CALLBACK(channel_message), NULL);
        g_signal_connect(channel, "on-open", G_CALLBACK(channel_open), NULL);
        g_signal_connect(channel, "on-close", G_CALLBACK(channel_closed), NULL);
    }
    g_free(label);
}
static GstPadProbeReturn count_buffer(GstPad *pad, GstPadProbeInfo *info, gpointer data) {
    (void)pad; (void)info;
    g_mutex_lock(&count_lock); if (data) encoded_count++; else raw_count++; g_mutex_unlock(&count_lock);
    return GST_PAD_PROBE_OK;
}
static gboolean health(gpointer unused) {
    (void)unused; gint64 raw, encoded;
    g_mutex_lock(&count_lock); raw = raw_count; encoded = encoded_count; g_mutex_unlock(&count_lock);
    JsonBuilder *b = message("health"); integer_field(b, "capturedFrames", raw); integer_field(b, "encodedFrames", encoded);
    integer_field(b, "timestampUs", g_get_monotonic_time()); finish(b); return G_SOURCE_CONTINUE;
}
static gboolean stop_loop(gpointer unused) { (void)unused; g_atomic_int_set(&stopping, 1); g_main_loop_quit(loop); return G_SOURCE_REMOVE; }
static gboolean command(gpointer raw) {
    char *line = raw; JsonParser *parser = json_parser_new(); GError *error = NULL;
    if (!json_parser_load_from_data(parser, line, -1, &error) || !JSON_NODE_HOLDS_OBJECT(json_parser_get_root(parser))) {
        fail("Invalid worker command JSON."); g_clear_error(&error); goto done;
    }
    JsonObject *obj = json_node_get_object(json_parser_get_root(parser));
    const char *type = json_object_get_string_member_with_default(obj, "type", "");
    if (strcmp(type, "stop") == 0) stop_loop(NULL);
    else if (strcmp(type, "offer") == 0) {
        const char *sdp = json_object_get_string_member_with_default(obj, "sdp", "");
        if (!g_atomic_int_compare_and_exchange(&offer_received, 0, 1) || strlen(sdp) > 262144) { fail("Duplicate or oversized offer."); goto done; }
        GstSDPMessage *message_sdp = NULL;
        if (gst_sdp_message_new(&message_sdp) != GST_SDP_OK || gst_sdp_message_parse_buffer((const guint8 *)sdp, (guint)strlen(sdp), message_sdp) != GST_SDP_OK) {
            if (message_sdp) gst_sdp_message_free(message_sdp);
            fail("Invalid SDP offer."); goto done;
        }
        GstWebRTCSessionDescription *offer = gst_webrtc_session_description_new(GST_WEBRTC_SDP_TYPE_OFFER, message_sdp);
        GstPromise *promise = gst_promise_new_with_change_func(remote_set, NULL, NULL);
        g_signal_emit_by_name(peer, "set-remote-description", offer, promise); gst_webrtc_session_description_free(offer);
    } else if (strcmp(type, "ice") == 0) {
        gint64 index = json_object_get_int_member_with_default(obj, "sdpMLineIndex", -1);
        const char *candidate = json_object_get_string_member_with_default(obj, "candidate", "");
        if (index < 0 || index > 16 || strlen(candidate) > 8192) { fail("Invalid ICE candidate."); goto done; }
        g_signal_emit_by_name(peer, "add-ice-candidate", (guint)index, candidate);
    } else if (strcmp(type, "send") == 0) {
        const char *text = json_object_get_string_member_with_default(obj, "data", "");
        g_mutex_lock(&channel_lock);
        if (strlen(text) > DATA_LIMIT || !control_channel || !gst_webrtc_data_channel_send_string_full(control_channel, text, &error))
            fail("Could not send controller receipt.");
        g_mutex_unlock(&channel_lock); g_clear_error(&error);
    } else fail("Unsupported worker command.");
done:
    g_object_unref(parser); g_free(line); return G_SOURCE_REMOVE;
}
static gpointer read_input(gpointer unused) {
    (void)unused;
    char chunk[2048]; GString *line = g_string_sized_new(2048);
    while (!g_atomic_int_get(&stopping)) {
#ifdef _WIN32
        int count = _read(0, chunk, sizeof(chunk));
#else
        ssize_t count = read(0, chunk, sizeof(chunk));
#endif
        if (count <= 0) break;
        for (size_t i = 0; i < (size_t)count; i++) {
            if (chunk[i] == '\n') {
                g_idle_add(command, g_strdup(line->str)); g_string_truncate(line, 0);
            } else if (chunk[i] == 0 || line->len >= LINE_LIMIT) {
                fail("Oversized or invalid worker protocol line."); goto done;
            } else g_string_append_c(line, chunk[i]);
        }
    }
    if (line->len) fail("Worker closed a partial command.");
done:
    g_string_free(line, TRUE); g_idle_add(stop_loop, NULL); return NULL;
}
static gboolean verify_window(guint64 window, guint64 pid, guint *width, guint *height) {
    if (window == 0 || pid == 0) return FALSE;
#ifdef _WIN32
    HWND hwnd = (HWND)(uintptr_t)window; DWORD actual = 0; RECT client;
    GetWindowThreadProcessId(hwnd, &actual);
    if (actual != pid || !IsWindowVisible(hwnd) || IsIconic(hwnd) || !GetClientRect(hwnd, &client)) return FALSE;
    *width = (guint)(client.right - client.left); *height = (guint)(client.bottom - client.top);
#else
    const char *display_name = g_getenv("DISPLAY");
    if (!display_name || !(display_name[0] == ':' || g_str_has_prefix(display_name, "unix:"))) return FALSE;
    Display *display = XOpenDisplay(NULL); if (!display) return FALSE;
    Atom atom = XInternAtom(display, "_NET_WM_PID", True), actual_type; int actual_format;
    unsigned long length = 0, after = 0; unsigned char *data = NULL; XWindowAttributes attributes;
    gboolean good = atom != None && XGetWindowProperty(display, (Window)window, atom, 0, 1, False, XA_CARDINAL,
        &actual_type, &actual_format, &length, &after, &data) == Success && actual_type == XA_CARDINAL && actual_format == 32 && length == 1 &&
        data && *(unsigned long *)data == pid && XGetWindowAttributes(display, (Window)window, &attributes) && attributes.map_state == IsViewable;
    if (good) { *width = (guint)attributes.width; *height = (guint)attributes.height; }
    if (data) XFree(data);
    XCloseDisplay(display);
    if (!good) return FALSE;
#endif
    return *width > 0 && *height > 0 && *width <= 32768 && *height <= 32768;
}
static gboolean snapshot_timeout(gpointer unused) { (void)unused; fail("Window screenshot timed out."); return G_SOURCE_REMOVE; }
int main(int argc, char **argv) {
#ifndef _WIN32
    XInitThreads();
#endif
    guint64 window = 0, pid = 0; gboolean fixture = FALSE; const char *png_path = NULL;
    for (int i = 1; i < argc; i++) {
        if (strcmp(argv[i], "--fixture") == 0) fixture = TRUE;
        else if (strcmp(argv[i], "--screenshot") == 0 && i + 1 < argc) { png_path = argv[++i]; screenshot_mode = TRUE; }
        else if (strcmp(argv[i], "--window") == 0 && i + 1 < argc) window = g_ascii_strtoull(argv[++i], NULL, 16);
        else if (strcmp(argv[i], "--pid") == 0 && i + 1 < argc) pid = g_ascii_strtoull(argv[++i], NULL, 10);
        else { fputs("Unknown worker option.\n", stderr); return 2; }
    }
    guint width = 1280, height = 720;
    if ((!fixture && !verify_window(window, pid, &width, &height)) || (fixture && (window || pid))) {
        fputs("Refusing capture: target must be a visible window of the owned process.\n", stderr); return 2;
    }
    gst_init(NULL, NULL); loop = g_main_loop_new(NULL, FALSE);
    gchar *source;
    if (fixture) source = g_strdup("videotestsrc is-live=true name=source pattern=ball ! video/x-raw,width=1280,height=720,framerate=60/1");
#ifdef _WIN32
    else source = g_strdup_printf("d3d11screencapturesrc name=source capture-api=wgc window-handle=%" G_GUINT64_FORMAT " window-capture-mode=client show-cursor=true ! d3d11download", window);
#else
    else source = g_strdup_printf("ximagesrc name=source xid=%" G_GUINT64_FORMAT " use-damage=false show-pointer=true do-timestamp=true ! video/x-raw,framerate=60/1", window);
#endif
    gchar *description = g_strdup_printf("%s ! queue max-size-buffers=2 max-size-bytes=0 max-size-time=0 leaky=downstream ! videoconvert ! videoscale add-borders=true ! video/x-raw,format=I420,width=1280,height=720,pixel-aspect-ratio=1/1 ! identity name=raw ! x264enc name=encoder tune=zerolatency speed-preset=ultrafast bitrate=6000 key-int-max=60 bframes=0 ! video/x-h264,profile=constrained-baseline ! identity name=encoded ! rtph264pay config-interval=-1 pt=96 aggregate-mode=zero-latency ! application/x-rtp,media=video,encoding-name=H264,payload=96,clock-rate=90000 ! webrtcbin name=peer bundle-policy=max-bundle", source);
    if (screenshot_mode) { g_free(description); description = g_strdup_printf("%s ! videoconvert ! pngenc snapshot=true ! filesink name=image", source); }
    GError *error = NULL; pipeline = gst_parse_launch(description, &error); g_free(description); g_free(source);
    if (!pipeline || error) { fail(error ? error->message : "Media pipeline creation failed."); g_clear_error(&error); return 3; }
    if (screenshot_mode) {
        GstElement *image = gst_bin_get_by_name(GST_BIN(pipeline), "image");
        g_object_set(image, "location", png_path, NULL); gst_object_unref(image);
        GstElement *capture = gst_bin_get_by_name(GST_BIN(pipeline), "source");
        g_object_set(capture, "num-buffers", 1, NULL); gst_object_unref(capture);
        GstBus *bus = gst_element_get_bus(pipeline); guint watch = gst_bus_add_watch(bus, bus_message, NULL); gst_object_unref(bus);
        if (gst_element_set_state(pipeline, GST_STATE_PLAYING) == GST_STATE_CHANGE_FAILURE) { fail("Screenshot capture did not start."); return 3; }
        guint deadline = g_timeout_add_seconds(10, snapshot_timeout, NULL); g_main_loop_run(loop);
        g_source_remove(watch);
        if (!g_atomic_int_get(&failed)) g_source_remove(deadline);
        gst_element_set_state(pipeline, GST_STATE_NULL); gst_object_unref(pipeline);
        if (!g_atomic_int_get(&failed)) {
            JsonBuilder *done = message("snapshot"); integer_field(done, "sourceWidth", width); integer_field(done, "sourceHeight", height); finish(done);
        }
        return g_atomic_int_get(&failed) ? 3 : 0;
    }
    peer = gst_bin_get_by_name(GST_BIN(pipeline), "peer");
    g_signal_connect(peer, "on-ice-candidate", G_CALLBACK(ice_candidate), NULL);
    g_signal_connect(peer, "prepare-data-channel", G_CALLBACK(prepare_channel), NULL);
    GstBus *bus = gst_element_get_bus(pipeline); guint watch = gst_bus_add_watch(bus, bus_message, NULL); gst_object_unref(bus);
    const char *names[] = { "raw", "encoded" };
    for (int i = 0; i < 2; i++) {
        GstElement *element = gst_bin_get_by_name(GST_BIN(pipeline), names[i]); GstPad *pad = gst_element_get_static_pad(element, "src");
        gst_pad_add_probe(pad, GST_PAD_PROBE_TYPE_BUFFER, count_buffer, GINT_TO_POINTER(i), NULL); gst_object_unref(pad); gst_object_unref(element);
    }
    if (gst_element_set_state(pipeline, GST_STATE_PLAYING) == GST_STATE_CHANGE_FAILURE) { fail("Media pipeline did not enter PLAYING."); return 3; }
    JsonBuilder *ready = message("ready"); integer_field(ready, "protocolVersion", 1); integer_field(ready, "sourceWidth", width); integer_field(ready, "sourceHeight", height);
    integer_field(ready, "width", 1280); integer_field(ready, "height", 720); integer_field(ready, "fps", 60); finish(ready);
    GThread *input_thread = g_thread_new("commands", read_input, NULL); g_thread_unref(input_thread);
    guint timer = g_timeout_add(1000, health, NULL); g_main_loop_run(loop);
    g_atomic_int_set(&stopping, 1); g_source_remove(timer); g_source_remove(watch);
    gst_element_set_state(pipeline, GST_STATE_NULL);
    g_mutex_lock(&channel_lock); g_clear_object(&control_channel); g_clear_object(&state_channel); g_mutex_unlock(&channel_lock);
    gst_object_unref(peer); gst_object_unref(pipeline);
    /* The input thread can be blocked in the OS pipe. Process exit releases it; the supervisor owns the shutdown deadline. */
    return g_atomic_int_get(&failed) ? 3 : 0;
}
