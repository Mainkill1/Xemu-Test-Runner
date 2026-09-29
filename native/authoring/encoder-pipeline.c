/* SPDX-License-Identifier: MIT
 * Explicit low-latency H.264 backends. Unsupported properties/drivers fail the
 * actual encode probe; they are never silently removed to make a backend pass.
 */
#include "encoder-pipeline.h"
#include <string.h>

static const EncoderBackend backends[] = {
    { "nvenc", "nvh264enc", "NV12",
      "rc-mode=cbr bframes=0 rc-lookahead=0 zerolatency=true gop-size=60", TRUE },
    { "qsv", "qsvh264enc", "NV12",
      "rate-control=cbr b-frames=0 rc-lookahead=0 gop-size=60", TRUE },
    { "amf", "amfh264enc", "NV12",
      "usage=ultra-low-latency preset=speed rate-control=cbr b-frames=0 gop-size=60", TRUE },
    { "vaapi", "vah264enc", "NV12",
      "rate-control=cbr b-frames=0 cabac=false dct8x8=false key-int-max=60", TRUE },
    { "x264", "x264enc", "I420",
      "tune=zerolatency speed-preset=ultrafast bframes=0 rc-lookahead=0 "
      "sync-lookahead=0 sliced-threads=true key-int-max=60 vbv-buf-capacity=100", FALSE }
};

const EncoderBackend *media_encoders(gsize *count) {
    *count = G_N_ELEMENTS(backends);
    return backends;
}
const EncoderBackend *media_find_encoder(const char *id) {
    for (gsize i = 0; i < G_N_ELEMENTS(backends); i++)
        if (g_strcmp0(id, backends[i].id) == 0) return &backends[i];
    return NULL;
}
gboolean media_encoder_installed(const EncoderBackend *backend) {
    GstElementFactory *factory = gst_element_factory_find(backend->factory);
    if (!factory) return FALSE;
    gst_object_unref(factory);
    return TRUE;
}
gchar *media_encoder_pipeline(const EncoderBackend *backend, const MediaConfig *config) {
    /* Both the probe and live pipeline use this exact fragment. No leaky queue
     * after encoding: dropping arbitrary reference frames corrupts H.264. */
    return g_strdup_printf(
        "videoconvert ! videoscale add-borders=true "
        "! video/x-raw,format=%s,width=%u,height=%u,pixel-aspect-ratio=1/1 "
        "! identity name=raw ! %s name=encoder %s bitrate=%u "
        "! video/x-h264,profile=constrained-baseline "
        "! h264parse config-interval=-1 "
        "! video/x-h264,stream-format=byte-stream,alignment=au "
        "! identity name=encoded", backend->format, config->width, config->height,
        backend->factory, backend->settings, config->bitrate_kbps);
}
static GstPadProbeReturn probe_buffer(GstPad *pad, GstPadProbeInfo *info, gpointer data) {
    (void)pad;
    EncoderProbe *probe = data;
    GstBuffer *buffer = GST_PAD_PROBE_INFO_BUFFER(info);
    if (buffer && gst_buffer_get_size(buffer)) {
        probe->frames++;
        probe->bytes += gst_buffer_get_size(buffer);
    }
    return GST_PAD_PROBE_OK;
}
EncoderProbe media_probe_encoder(const EncoderBackend *backend, const MediaConfig *config) {
    EncoderProbe result = {0};
    if (!media_encoder_installed(backend)) {
        g_strlcpy(result.reason, "Encoder plugin/device is not available.", sizeof(result.reason));
        return result;
    }
    gchar *encoder = media_encoder_pipeline(backend, config);
    gchar *description = g_strdup_printf(
        "videotestsrc num-buffers=120 pattern=smpte ! "
        "video/x-raw,width=%u,height=%u,framerate=%u/1 ! %s ! fakesink sync=false",
        config->width, config->height, config->fps, encoder);
    GError *error = NULL;
    GstElement *test = gst_parse_launch(description, &error);
    g_free(description); g_free(encoder);
    if (!test || error) {
        g_strlcpy(result.reason, error ? error->message : "Encoder pipeline creation failed.", sizeof(result.reason));
        g_clear_error(&error);
        if (test) gst_object_unref(test);
        return result;
    }
    GstElement *encoded = gst_bin_get_by_name(GST_BIN(test), "encoded");
    GstPad *pad = gst_element_get_static_pad(encoded, "src");
    gst_pad_add_probe(pad, GST_PAD_PROBE_TYPE_BUFFER, probe_buffer, &result, NULL);
    gst_object_unref(pad); gst_object_unref(encoded);
    GstBus *bus = gst_element_get_bus(test);
    gint64 start = g_get_monotonic_time();
    GstMessage *done = NULL;
    if (gst_element_set_state(test, GST_STATE_PLAYING) != GST_STATE_CHANGE_FAILURE)
        done = gst_bus_timed_pop_filtered(bus, 8 * GST_SECOND, GST_MESSAGE_EOS | GST_MESSAGE_ERROR);
    gint64 elapsed = MAX(g_get_monotonic_time() - start, 1);
    gst_element_set_state(test, GST_STATE_NULL);
    if (done && GST_MESSAGE_TYPE(done) == GST_MESSAGE_EOS) {
        result.frames_per_second = result.frames * 1000000.0 / elapsed;
        result.passed = result.frames == 120 && result.bytes > 0 && result.frames_per_second >= 55.0;
        g_strlcpy(result.reason, result.passed ? "Synthetic encode capacity passed; browser qualification still required." :
            "Encoder did not produce 120 H.264 access units at the 55 FPS capacity floor.", sizeof(result.reason));
    } else if (done) {
        gchar *debug = NULL;
        gst_message_parse_error(done, &error, &debug);
        g_strlcpy(result.reason, error ? error->message : "Encoder failed.", sizeof(result.reason));
        g_clear_error(&error); g_free(debug);
    } else {
        g_strlcpy(result.reason, "Encoder failed to start or exceeded the eight-second probe deadline.", sizeof(result.reason));
    }
    if (done) gst_message_unref(done);
    gst_object_unref(bus); gst_object_unref(test);
    return result;
}
