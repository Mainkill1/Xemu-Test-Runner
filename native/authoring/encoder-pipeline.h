/* SPDX-License-Identifier: MIT */
#ifndef XEMU_ENCODER_PIPELINE_H
#define XEMU_ENCODER_PIPELINE_H
#include <gst/gst.h>

typedef struct {
    const char *id;
    const char *factory;
    const char *format;
    const char *settings;
    gboolean hardware;
} EncoderBackend;

typedef struct {
    guint width, height, fps, bitrate_kbps;
} MediaConfig;

typedef struct {
    gboolean passed;
    guint64 frames, bytes;
    gdouble frames_per_second;
    gchar reason[512];
} EncoderProbe;

const EncoderBackend *media_encoders(gsize *count);
const EncoderBackend *media_find_encoder(const char *id);
gboolean media_encoder_installed(const EncoderBackend *backend);
gchar *media_encoder_pipeline(const EncoderBackend *backend, const MediaConfig *config);
/* Runs actual source -> encoder -> parser -> fakesink, not a plugin-presence check.
 * This measures synthetic encode capacity. It never qualifies browser latency. */
EncoderProbe media_probe_encoder(const EncoderBackend *backend, const MediaConfig *config);
#endif
