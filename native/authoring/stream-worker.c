/* Real-time media only. The runner owns target lifetime, authorization, input and evidence.
 * SPDX-License-Identifier: MIT
 * Copyright (c) 2026 Xemu Test Runner contributors
 */
#include <gst/gst.h>
#include <gst/sdp/sdp.h>
#include <gst/webrtc/webrtc.h>
#include <gst/video/video-event.h>
#include "encoder-pipeline.h"
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
#define COMMAND_COUNT_LIMIT 128
#define COMMAND_BYTES_LIMIT (2 * 1024 * 1024)
typedef struct { gchar *text; gsize bytes; } QueuedCommand;
static GMutex command_lock;
static guint command_count;
static gsize command_bytes;
static GMainLoop *loop;
static GstElement *pipeline, *peer;
static GstWebRTCDataChannel *control_channel, *state_channel;
static GMutex output_lock, channel_lock;
static gint failed, stopping, offer_received;
static gboolean screenshot_mode;
static guint video_payload;
static guint source_width, source_height;
static gint64 surface_version = 1;
static MediaConfig config = { 1280, 720, 60, 6000 };
static const EncoderBackend *selected_encoder;
static guint handshake_timeout_ms = 15000;
static gint64 started_us;
static gint64 capture_count, encoded_bytes, keyframe_count, last_capture_us, last_encode_us;
static gint64 previous_capture, previous_encoded, previous_bytes, previous_health_us;
static gint remote_ready;
typedef struct { guint index; gchar *candidate; } PendingIce;
static PendingIce pending_ice[128];
static guint pending_ice_count;
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
static void double_field(JsonBuilder *b, const char *key, gdouble value) {
    json_builder_set_member_name(b, key); json_builder_add_double_value(b, value);
}
static void bool_field(JsonBuilder *b, const char *key, gboolean value) {
    json_builder_set_member_name(b, key); json_builder_add_boolean_value(b, value);
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
        if (debug) g_printerr("GStreamer: %s\n", debug);
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
static gboolean flush_ice(gpointer unused) {
    (void)unused;
    for (guint i = 0; i < pending_ice_count; i++) {
        g_signal_emit_by_name(peer, "add-ice-candidate", pending_ice[i].index, pending_ice[i].candidate);
        g_free(pending_ice[i].candidate);
    }
    pending_ice_count = 0;
    return G_SOURCE_REMOVE;
}
static void remote_set(GstPromise *promise, gpointer unused) {
    (void)unused;
    if (!promise_ok(promise)) { fail("WebRTC set-remote-description failed."); gst_promise_unref(promise); return; }
    gst_promise_unref(promise);
    g_atomic_int_set(&remote_ready, 1);
    g_idle_add(flush_ice, NULL);
    GstPromise *next = gst_promise_new_with_change_func(answer_created, NULL, NULL);
    g_signal_emit_by_name(peer, "create-answer", NULL, next);
}
static void channel_message(GstWebRTCDataChannel *channel, gchar *text, gpointer unused) {
    (void)unused;
    if (g_atomic_int_get(&stopping)) return;
    g_mutex_lock(&channel_lock);
    gboolean authorized = channel == control_channel || channel == state_channel;
    g_mutex_unlock(&channel_lock);
    if (!authorized) { fail("Data received on an unvalidated channel."); return; }
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
static void register_channel(GstElement *element, GstWebRTCDataChannel *channel, gpointer unused) {
    (void)element; (void)unused;
    gchar *label = NULL; gboolean ordered = FALSE; gint retransmits = -1, lifetime = -1;
    g_object_get(channel, "label", &label, "ordered", &ordered, "max-retransmits", &retransmits, "max-packet-lifetime", &lifetime, NULL);
    gboolean valid = FALSE;
    g_mutex_lock(&channel_lock);
    if (g_strcmp0(label, "session-control") == 0 && ordered && retransmits == -1 && lifetime == -1 && !control_channel) {
        control_channel = g_object_ref(channel); valid = TRUE;
    } else if (g_strcmp0(label, "input-state") == 0 && !ordered && retransmits == 0 && lifetime == -1 && !state_channel) {
        state_channel = g_object_ref(channel); valid = TRUE;
    }
    g_mutex_unlock(&channel_lock);
    if (!valid) { gst_webrtc_data_channel_close(channel); fail("Unexpected or incorrectly configured data channel."); }
    g_free(label);
}
/* prepare-data-channel precedes DCEP parsing, so label/reliability can still
 * be unset. Attach handlers here, validate the negotiated channel afterward. */
static void prepare_channel(GstElement *element, GstWebRTCDataChannel *channel, gboolean local, gpointer unused) {
    (void)element; (void)local; (void)unused;
    g_signal_connect(channel, "on-message-string", G_CALLBACK(channel_message), NULL);
    g_signal_connect(channel, "on-open", G_CALLBACK(channel_open), NULL);
    g_signal_connect(channel, "on-close", G_CALLBACK(channel_closed), NULL);
}
static GstPadProbeReturn count_buffer(GstPad *pad, GstPadProbeInfo *info, gpointer data) {
    (void)pad;
    gint kind = GPOINTER_TO_INT(data);
    /* Publish geometry before buffers carrying the new CAPS are forwarded.
     * The controller owner must reject coordinates from the old version. */
    if (GST_PAD_PROBE_INFO_TYPE(info) & GST_PAD_PROBE_TYPE_EVENT_DOWNSTREAM) {
        GstEvent *event = GST_PAD_PROBE_INFO_EVENT(info);
        if (kind == 0 && GST_EVENT_TYPE(event) == GST_EVENT_CAPS) {
            GstCaps *caps = NULL;
            gint width = 0, height = 0;
            gst_event_parse_caps(event, &caps);
            const GstStructure *structure = gst_caps_get_structure(caps, 0);
            if (gst_structure_get_int(structure, "width", &width) &&
                gst_structure_get_int(structure, "height", &height) && width > 0 && height > 0) {
                g_mutex_lock(&count_lock);
                gboolean changed = source_width != (guint)width || source_height != (guint)height;
                if (changed) { source_width = (guint)width; source_height = (guint)height; surface_version++; }
                gint64 version = surface_version;
                g_mutex_unlock(&count_lock);
                if (changed) {
                    JsonBuilder *b = message("surface-changed");
                    integer_field(b, "sourceWidth", width); integer_field(b, "sourceHeight", height);
                    integer_field(b, "surfaceVersion", version);
                    integer_field(b, "width", config.width); integer_field(b, "height", config.height);
                    finish(b);
                }
            }
        }
        return GST_PAD_PROBE_OK;
    }
    GstBuffer *buffer = GST_PAD_PROBE_INFO_BUFFER(info);
    g_mutex_lock(&count_lock);
    if (kind == 0) { capture_count++; last_capture_us = g_get_monotonic_time(); }
    else if (kind == 1) raw_count++;
    else {
        encoded_count++;
        encoded_bytes += (gint64)gst_buffer_get_size(buffer);
        last_encode_us = g_get_monotonic_time();
        if (!GST_BUFFER_FLAG_IS_SET(buffer, GST_BUFFER_FLAG_DELTA_UNIT)) keyframe_count++;
    }
    g_mutex_unlock(&count_lock);
    return GST_PAD_PROBE_OK;
}
static gboolean health(gpointer unused) {
    (void)unused;
    GstWebRTCPeerConnectionState connection;
    g_object_get(peer, "connection-state", &connection, NULL);
    gint64 now = g_get_monotonic_time();
    if (connection == GST_WEBRTC_PEER_CONNECTION_STATE_FAILED ||
        connection == GST_WEBRTC_PEER_CONNECTION_STATE_CLOSED ||
        connection == GST_WEBRTC_PEER_CONNECTION_STATE_DISCONNECTED) {
        fail("WebRTC transport disconnected; the authoring segment must be invalidated.");
        return G_SOURCE_CONTINUE;
    }
    if (connection != GST_WEBRTC_PEER_CONNECTION_STATE_CONNECTED &&
        now - started_us > (gint64)handshake_timeout_ms * 1000) {
        fail("WebRTC offer/connection exceeded the handshake deadline.");
        return G_SOURCE_CONTINUE;
    }
    JsonBuilder *b = message("health");
    g_mutex_lock(&count_lock);
    gdouble seconds = MAX(now - previous_health_us, 1) / 1000000.0;
    integer_field(b, "surfaceVersion", surface_version);
    integer_field(b, "sourceWidth", source_width); integer_field(b, "sourceHeight", source_height);
    integer_field(b, "capturedFrames", capture_count);
    integer_field(b, "encoderInputFrames", raw_count);
    integer_field(b, "encodedFrames", encoded_count);
    integer_field(b, "encodedBytes", encoded_bytes);
    integer_field(b, "keyframes", keyframe_count);
    double_field(b, "captureFps", (capture_count - previous_capture) / seconds);
    double_field(b, "encodeFps", (encoded_count - previous_encoded) / seconds);
    double_field(b, "encodedKbps", (encoded_bytes - previous_bytes) * 8.0 / seconds / 1000.0);
    integer_field(b, "lastCaptureAgeMs", last_capture_us ? (now - last_capture_us) / 1000 : -1);
    integer_field(b, "lastEncodeAgeMs", last_encode_us ? (now - last_encode_us) / 1000 : -1);
    previous_capture = capture_count; previous_encoded = encoded_count;
    previous_bytes = encoded_bytes; previous_health_us = now;
    g_mutex_unlock(&count_lock);
    integer_field(b, "timestampUs", now);
    integer_field(b, "payloadType", video_payload);
    integer_field(b, "targetBitrateKbps", config.bitrate_kbps);
    bool_field(b, "peerConnected", connection == GST_WEBRTC_PEER_CONNECTION_STATE_CONNECTED);
    /* Capture/encoded counts are media samples, never emulated guest frames. */
    bool_field(b, "browserQualified", FALSE);
    finish(b);
    return G_SOURCE_CONTINUE;
}
static gboolean stop_loop(gpointer unused) { (void)unused; g_atomic_int_set(&stopping, 1); g_main_loop_quit(loop); return G_SOURCE_REMOVE; }
/* H.264 payload numbers belong to the offer, not to a fixed encoder preset.
 * Setting the payloader and RTP caps together prevents an otherwise valid
 * answer from leaving the video transceiver inactive. */
static gboolean configure_offered_video(const GstSDPMessage *sdp) {
    if (gst_sdp_message_medias_len(sdp) != 2) return FALSE;
    const GstSDPMedia *video = gst_sdp_message_get_media(sdp, 0);
    const GstSDPMedia *data = gst_sdp_message_get_media(sdp, 1);
    if (g_strcmp0(gst_sdp_media_get_media(video), "video") ||
        g_strcmp0(gst_sdp_media_get_media(data), "application")) return FALSE;
    for (guint i = 0; i < gst_sdp_media_formats_len(video); i++) {
        gchar *end = NULL;
        guint64 pt = g_ascii_strtoull(gst_sdp_media_get_format(video, i), &end, 10);
        if (!end || *end || pt < 96 || pt > 127) continue;
        GstCaps *offered = gst_sdp_media_get_caps_from_media(video, (gint)pt);
        if (!offered || gst_caps_is_empty(offered)) {
            if (offered) gst_caps_unref(offered);
            continue;
        }
        const GstStructure *c = gst_caps_get_structure(offered, 0);
        const gchar *profile = gst_structure_get_string(c, "profile");
        gboolean compatible = g_strcmp0(gst_structure_get_string(c, "encoding-name"), "H264") == 0 &&
            g_strcmp0(gst_structure_get_string(c, "packetization-mode"), "1") == 0 &&
            g_strcmp0(profile, "constrained-baseline") == 0;
        gst_caps_unref(offered);
        if (!compatible) continue;
        video_payload = (guint)pt;
        GstElement *pay = gst_bin_get_by_name(GST_BIN(pipeline), "pay");
        GstElement *filter = gst_bin_get_by_name(GST_BIN(pipeline), "rtpfilter");
        GstCaps *caps = gst_caps_new_simple("application/x-rtp",
            "media", G_TYPE_STRING, "video", "encoding-name", G_TYPE_STRING, "H264",
            "clock-rate", G_TYPE_INT, 90000, "payload", G_TYPE_INT, (gint)pt,
            "packetization-mode", G_TYPE_STRING, "1", "profile", G_TYPE_STRING, "constrained-baseline", NULL);
        g_object_set(pay, "pt", (guint)pt, NULL);
        g_object_set(filter, "caps", caps, NULL);
        GstWebRTCRTPTransceiver *transceiver = NULL;
        g_signal_emit_by_name(peer, "get-transceiver", 0, &transceiver);
        if (transceiver) {
            g_object_set(transceiver, "direction", GST_WEBRTC_RTP_TRANSCEIVER_DIRECTION_SENDONLY,
                "codec-preferences", caps, NULL);
            gst_object_unref(transceiver);
        }
        gst_caps_unref(caps); gst_object_unref(pay); gst_object_unref(filter);
        return TRUE;
    }
    return FALSE;
}
static const char *command_string(JsonObject *obj, const char *name) {
    JsonNode *node = json_object_get_member(obj, name);
    if (!node || !JSON_NODE_HOLDS_VALUE(node) || json_node_get_value_type(node) != G_TYPE_STRING) {
        fail("Worker command requires a string member of the declared type."); return NULL;
    }
    return json_node_get_string(node);
}
static gint64 command_integer(JsonObject *obj, const char *name) {
    JsonNode *node = json_object_get_member(obj, name);
    if (!node || !JSON_NODE_HOLDS_VALUE(node) || json_node_get_value_type(node) != G_TYPE_INT64) {
        fail("Worker command requires an integer member of the declared type."); return -1;
    }
    return json_node_get_int(node);
}
static gboolean command(gpointer raw) {
    QueuedCommand *item = raw;
    char *line = item->text; JsonParser *parser = json_parser_new(); GError *error = NULL;
    if (g_atomic_int_get(&stopping) || g_atomic_int_get(&failed)) goto done;
    if (!json_parser_load_from_data(parser, line, -1, &error) || !JSON_NODE_HOLDS_OBJECT(json_parser_get_root(parser))) {
        fail("Invalid worker command JSON."); g_clear_error(&error); goto done;
    }
    JsonObject *obj = json_node_get_object(json_parser_get_root(parser));
    const char *type = command_string(obj, "type");
    if (!type) goto done;
    if (strcmp(type, "stop") == 0) stop_loop(NULL);
    else if (strcmp(type, "offer") == 0) {
        const char *sdp = command_string(obj, "sdp");
        if (!sdp) goto done;
        if (!g_atomic_int_compare_and_exchange(&offer_received, 0, 1) || strlen(sdp) > 262144) { fail("Duplicate or oversized offer."); goto done; }
        GstSDPMessage *message_sdp = NULL;
        if (gst_sdp_message_new(&message_sdp) != GST_SDP_OK || gst_sdp_message_parse_buffer((const guint8 *)sdp, (guint)strlen(sdp), message_sdp) != GST_SDP_OK) {
            if (message_sdp) gst_sdp_message_free(message_sdp);
            fail("Invalid SDP offer."); goto done;
        }
        if (!configure_offered_video(message_sdp)) {
            gst_sdp_message_free(message_sdp);
            fail("Offer must receive one constrained-baseline H.264 packetization-mode=1 video track and data channels."); goto done;
        }
        if (gst_element_set_state(pipeline, GST_STATE_PLAYING) == GST_STATE_CHANGE_FAILURE) {
            gst_sdp_message_free(message_sdp); fail("Media pipeline did not enter PLAYING."); goto done;
        }
        GstWebRTCSessionDescription *offer = gst_webrtc_session_description_new(GST_WEBRTC_SDP_TYPE_OFFER, message_sdp);
        GstPromise *promise = gst_promise_new_with_change_func(remote_set, NULL, NULL);
        g_signal_emit_by_name(peer, "set-remote-description", offer, promise); gst_webrtc_session_description_free(offer);
    } else if (strcmp(type, "ice") == 0) {
        gint64 index = command_integer(obj, "sdpMLineIndex");
        const char *candidate = command_string(obj, "candidate");
        if (!candidate) goto done;
        if (index < 0 || index > 16 || strlen(candidate) > 8192) { fail("Invalid ICE candidate."); goto done; }
        if (!g_atomic_int_get(&remote_ready)) {
            if (pending_ice_count == G_N_ELEMENTS(pending_ice)) { fail("Too many pending ICE candidates."); goto done; }
            pending_ice[pending_ice_count++] = (PendingIce){ (guint)index, g_strdup(candidate) };
        } else g_signal_emit_by_name(peer, "add-ice-candidate", (guint)index, candidate);
    } else if (strcmp(type, "keyframe") == 0) {
        GstElement *encoder = gst_bin_get_by_name(GST_BIN(pipeline), "encoder");
        GstPad *pad = gst_element_get_static_pad(encoder, "src");
        gboolean accepted = gst_pad_send_event(pad,
            gst_video_event_new_upstream_force_key_unit(GST_CLOCK_TIME_NONE, TRUE, 0));
        gst_object_unref(pad); gst_object_unref(encoder);
        if (!accepted) fail("Encoder rejected a keyframe request.");
        else finish(message("keyframe-requested"));
    } else if (strcmp(type, "bitrate") == 0) {
        gint64 bitrate = command_integer(obj, "bitrateKbps");
        if (bitrate < 500 || bitrate > 30000) { fail("Bitrate must be 500..30000 kbit/s."); goto done; }
        GstElement *encoder = gst_bin_get_by_name(GST_BIN(pipeline), "encoder");
        GParamSpec *property = g_object_class_find_property(G_OBJECT_GET_CLASS(encoder), "bitrate");
        if (!property || !(property->flags & GST_PARAM_MUTABLE_PLAYING)) {
            gst_object_unref(encoder); fail("This encoder cannot change bitrate while streaming."); goto done;
        }
        g_object_set(encoder, "bitrate", (guint)bitrate, NULL);
        config.bitrate_kbps = (guint)bitrate; gst_object_unref(encoder);
        JsonBuilder *reply = message("encoder-config");
        integer_field(reply, "bitrateKbps", bitrate); finish(reply);
    } else if (strcmp(type, "send") == 0) {
        const char *text = command_string(obj, "data");
        if (!text) goto done;
        g_mutex_lock(&channel_lock);
        if (strlen(text) > DATA_LIMIT || !control_channel || !gst_webrtc_data_channel_send_string_full(control_channel, text, &error))
            fail("Could not send controller receipt.");
        g_mutex_unlock(&channel_lock); g_clear_error(&error);
    } else fail("Unsupported worker command.");
done:
    g_object_unref(parser); return G_SOURCE_REMOVE;
}
static void release_command(gpointer raw) {
    QueuedCommand *item = raw;
    g_mutex_lock(&command_lock);
    command_count--; command_bytes -= item->bytes;
    g_mutex_unlock(&command_lock);
    g_free(item->text); g_free(item);
}
static gboolean enqueue_command(const GString *line) {
    g_mutex_lock(&command_lock);
    if (command_count >= COMMAND_COUNT_LIMIT || line->len + 1 > COMMAND_BYTES_LIMIT - command_bytes) {
        g_mutex_unlock(&command_lock);
        fail("Worker command queue exceeded its bounded capacity.");
        return FALSE;
    }
    command_count++; command_bytes += line->len + 1;
    g_mutex_unlock(&command_lock);
    QueuedCommand *item = g_new(QueuedCommand, 1);
    item->text = g_strdup(line->str); item->bytes = line->len + 1;
    g_idle_add_full(G_PRIORITY_DEFAULT, command, item, release_command);
    return TRUE;
}
static gpointer read_input(gpointer unused) {
    (void)unused;
    char chunk[2048]; GString *line = g_string_sized_new(2048);
    while (!g_atomic_int_get(&stopping) && !g_atomic_int_get(&failed)) {
#ifdef _WIN32
        int count = _read(0, chunk, sizeof(chunk));
#else
        ssize_t count = read(0, chunk, sizeof(chunk));
#endif
        if (count <= 0) break;
        for (size_t i = 0; i < (size_t)count; i++) {
            if (chunk[i] == '\n') {
                if (!enqueue_command(line)) goto done;
                g_string_truncate(line, 0);
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
static gboolean parse_number(const char *text, guint base, guint64 min, guint64 max, guint64 *result) {
    if (!text || !*text || *text == '-' || *text == '+') return FALSE;
    for (const char *p = text; *p; p++)
        if ((base == 10 && !g_ascii_isdigit(*p)) || (base == 16 && !g_ascii_isxdigit(*p))) return FALSE;
    gchar *end = NULL;
    guint64 n = g_ascii_strtoull(text, &end, base);
    if (!end || *end || n < min || n > max) return FALSE;
    *result = n; return TRUE;
}
static void probe_fields(JsonBuilder *b, const EncoderBackend *backend, EncoderProbe probe) {
    string_field(b, "encoder", backend->id);
    string_field(b, "factory", backend->factory);
    bool_field(b, "hardware", backend->hardware);
    bool_field(b, "passed", probe.passed);
    bool_field(b, "browserQualified", FALSE);
    integer_field(b, "encodedFrames", (gint64)probe.frames);
    integer_field(b, "encodedBytes", (gint64)probe.bytes);
    double_field(b, "framesPerSecond", probe.frames_per_second);
    string_field(b, "reason", probe.reason);
}
int main(int argc, char **argv) {
#ifndef _WIN32
    XInitThreads();
#endif
    guint64 window = 0, pid = 0, value;
    gboolean fixture = FALSE, list = FALSE, probe_only = FALSE;
    const char *png_path = NULL, *encoder_id = "auto";
    for (int i = 1; i < argc; i++) {
        if (strcmp(argv[i], "--fixture") == 0) fixture = TRUE;
        else if (strcmp(argv[i], "--list-encoders") == 0) list = TRUE;
        else if (strcmp(argv[i], "--probe") == 0) probe_only = TRUE;
        else if (strcmp(argv[i], "--encoder") == 0 && i + 1 < argc) encoder_id = argv[++i];
        else if (strcmp(argv[i], "--screenshot") == 0 && i + 1 < argc) { png_path = argv[++i]; screenshot_mode = TRUE; }
        else if (strcmp(argv[i], "--window") == 0 && i + 1 < argc && parse_number(argv[++i], 16, 1, G_MAXUINT64 - 1, &window)) { }
        else if (strcmp(argv[i], "--pid") == 0 && i + 1 < argc && parse_number(argv[++i], 10, 1, G_MAXUINT32, &pid)) { }
        else if (strcmp(argv[i], "--bitrate-kbps") == 0 && i + 1 < argc && parse_number(argv[++i], 10, 500, 30000, &value)) config.bitrate_kbps = (guint)value;
        else if (strcmp(argv[i], "--handshake-timeout-ms") == 0 && i + 1 < argc && parse_number(argv[++i], 10, 100, 60000, &value)) handshake_timeout_ms = (guint)value;
        else if (strcmp(argv[i], "--resolution") == 0 && i + 1 < argc) {
            const char *size = argv[++i];
            if (strcmp(size, "1920x1080") == 0) { config.width = 1920; config.height = 1080; }
            else if (strcmp(size, "1280x720") != 0) { fputs("Only 1280x720 and 1920x1080 profiles are supported.\n", stderr); return 2; }
        } else { fputs("Invalid worker option/value.\n", stderr); return 2; }
    }
    if (strcmp(encoder_id, "auto") != 0 && !media_find_encoder(encoder_id)) {
        fputs("Unknown encoder backend.\n", stderr); return 2;
    }
    guint width = config.width, height = config.height;
    if (!list && !probe_only && ((!fixture && !verify_window(window, pid, &width, &height)) || (fixture && (window || pid)))) {
        fputs("Refusing capture: target must be a visible window of the owned process.\n", stderr); return 2;
    }
    source_width = width; source_height = height;
    gst_init(NULL, NULL);
    gsize backend_count;
    const EncoderBackend *backends = media_encoders(&backend_count);
    if (list) {
        JsonBuilder *b = message("encoders");
        json_builder_set_member_name(b, "items"); json_builder_begin_array(b);
        for (gsize i = 0; i < backend_count; i++) {
            json_builder_begin_object(b);
            string_field(b, "id", backends[i].id); string_field(b, "factory", backends[i].factory);
            bool_field(b, "hardware", backends[i].hardware);
            bool_field(b, "installed", media_encoder_installed(&backends[i]));
            bool_field(b, "qualified", FALSE); json_builder_end_object(b);
        }
        json_builder_end_array(b); finish(b); return 0;
    }
    EncoderProbe probe = {0};
    if (!screenshot_mode) {
        for (gsize i = 0; i < backend_count; i++) {
            if (strcmp(encoder_id, "auto") && strcmp(encoder_id, backends[i].id)) continue;
            probe = media_probe_encoder(&backends[i], &config);
            if (probe.passed) { selected_encoder = &backends[i]; break; }
            g_printerr("Encoder %s rejected: %s\n", backends[i].id, probe.reason);
            if (probe_only && strcmp(encoder_id, "auto")) {
                JsonBuilder *b = message("encoder-probe"); probe_fields(b, &backends[i], probe); finish(b);
            }
        }
        if (!selected_encoder) { fail("AUTHORING_MEDIA_UNAVAILABLE: no requested encoder passed the real H.264 probe."); return 3; }
        if (probe_only) {
            JsonBuilder *b = message("encoder-probe"); probe_fields(b, selected_encoder, probe);
            integer_field(b, "width", config.width); integer_field(b, "height", config.height); integer_field(b, "fps", config.fps);
            finish(b); return 0;
        }
    }
    loop = g_main_loop_new(NULL, FALSE);
    gchar *source;
    if (fixture) source = g_strdup_printf("videotestsrc is-live=true name=source pattern=ball ! video/x-raw,width=%u,height=%u,framerate=60/1", config.width, config.height);
#ifdef _WIN32
    else source = g_strdup_printf("d3d11screencapturesrc name=source capture-api=wgc window-handle=%" G_GUINT64_FORMAT " window-capture-mode=client show-cursor=true ! video/x-raw(memory:D3D11Memory),framerate=60/1 ! d3d11download", window);
#else
    else source = g_strdup_printf("ximagesrc name=source xid=%" G_GUINT64_FORMAT " use-damage=false show-pointer=true do-timestamp=true ! video/x-raw,framerate=60/1", window);
#endif
    gchar *description;
    if (screenshot_mode) description = g_strdup_printf("%s ! videoconvert ! pngenc snapshot=true ! filesink name=image", source);
    else {
        gchar *encoder = media_encoder_pipeline(selected_encoder, &config);
        description = g_strdup_printf("%s ! identity name=captured "
            "! queue max-size-buffers=2 max-size-bytes=0 max-size-time=0 leaky=downstream "
            "! %s ! rtph264pay name=pay mtu=1200 config-interval=-1 pt=96 aggregate-mode=zero-latency "
            "! capsfilter name=rtpfilter caps=application/x-rtp,media=video,encoding-name=H264,payload=96,clock-rate=90000 "
            "! webrtcbin name=peer bundle-policy=max-bundle", source, encoder);
        g_free(encoder);
    }
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
    g_signal_connect(peer, "on-data-channel", G_CALLBACK(register_channel), NULL);
    GstBus *bus = gst_element_get_bus(pipeline); guint watch = gst_bus_add_watch(bus, bus_message, NULL); gst_object_unref(bus);
    const char *names[] = { "captured", "raw", "encoded" };
    for (int i = 0; i < 3; i++) {
        GstElement *element = gst_bin_get_by_name(GST_BIN(pipeline), names[i]); GstPad *pad = gst_element_get_static_pad(element, "src");
        gst_pad_add_probe(pad, GST_PAD_PROBE_TYPE_BUFFER | (i == 0 ? GST_PAD_PROBE_TYPE_EVENT_DOWNSTREAM : 0),
            count_buffer, GINT_TO_POINTER(i), NULL); gst_object_unref(pad); gst_object_unref(element);
    }
    if (gst_element_set_state(pipeline, GST_STATE_READY) == GST_STATE_CHANGE_FAILURE) { fail("Media pipeline did not initialize."); return 3; }
    JsonBuilder *ready = message("ready"); integer_field(ready, "protocolVersion", 1); integer_field(ready, "sourceWidth", width); integer_field(ready, "sourceHeight", height); integer_field(ready, "surfaceVersion", surface_version);
    integer_field(ready, "width", config.width); integer_field(ready, "height", config.height); integer_field(ready, "fps", config.fps);
    string_field(ready, "stage", "signaling-ready"); string_field(ready, "encoder", selected_encoder->id);
    string_field(ready, "memoryPath", "system-memory"); bool_field(ready, "hardware", selected_encoder->hardware);
    bool_field(ready, "browserQualified", FALSE); integer_field(ready, "bitrateKbps", config.bitrate_kbps); finish(ready);
    started_us = previous_health_us = g_get_monotonic_time();
    GThread *input_thread = g_thread_new("commands", read_input, NULL); g_thread_unref(input_thread);
    guint timer = g_timeout_add(1000, health, NULL); g_main_loop_run(loop);
    g_atomic_int_set(&stopping, 1); g_source_remove(timer); g_source_remove(watch);
    gst_element_set_state(pipeline, GST_STATE_NULL);
    g_mutex_lock(&channel_lock); g_clear_object(&control_channel); g_clear_object(&state_channel); g_mutex_unlock(&channel_lock);
    gst_object_unref(peer); gst_object_unref(pipeline);
    for (guint i = 0; i < pending_ice_count; i++) g_free(pending_ice[i].candidate);
    /* The input thread can be blocked in the OS pipe. Process exit releases it; the supervisor owns the shutdown deadline. */
    return g_atomic_int_get(&failed) ? 3 : 0;
}
