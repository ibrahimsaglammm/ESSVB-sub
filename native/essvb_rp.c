/* Copyright (c) 2026 ESSVB. All Rights Reserved. */

#include "essvb_rp.h"

#include <chiaki/common.h>
#include <chiaki/discovery.h>
#include <chiaki/log.h>
#include <chiaki/regist.h>
#include <chiaki/session.h>
#include <chiaki/thread.h>
#include <chiaki/time.h>

#include <libavcodec/avcodec.h>
#include <libavutil/frame.h>
#include <libavutil/log.h>
#include <libavutil/mem.h>
#include <libswscale/swscale.h>

#include <stdbool.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#ifdef _WIN32
#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>
#ifndef SIO_UDP_CONNRESET
#define SIO_UDP_CONNRESET _WSAIOW(IOC_VENDOR, 12)
#endif
#else
#include <arpa/inet.h>
#include <netinet/in.h>
#include <pthread.h>
#include <time.h>
#endif

#ifndef CHIAKI_DISCOVERY_PORT_PS4
#define CHIAKI_DISCOVERY_PORT_PS4 987
#endif

#ifndef CHIAKI_DISCOVERY_PROTOCOL_VERSION_PS4
#define CHIAKI_DISCOVERY_PROTOCOL_VERSION_PS4 "00020020"
#endif

static ChiakiTarget resolve_target(int console_kind)
{
    if (console_kind == ESSVB_CONSOLE_PS5)
        return CHIAKI_TARGET_PS5_1;
#ifdef CHIAKI_TARGET_PS4_10
    return CHIAKI_TARGET_PS4_10;
#elif defined(CHIAKI_TARGET_PS4_9)
    return CHIAKI_TARGET_PS4_9;
#else
    return CHIAKI_TARGET_PS4_8;
#endif
}

static void sleep_ms(unsigned int ms)
{
#ifdef _WIN32
    Sleep(ms);
#else
    struct timespec ts = { (time_t)(ms / 1000), (long)(ms % 1000) * 1000000L };
    nanosleep(&ts, NULL);
#endif
}

static int map_log_level(ChiakiLogLevel level)
{
    if (level & CHIAKI_LOG_ERROR) return 0;
    if (level & CHIAKI_LOG_WARNING) return 1;
    return 2;
}

typedef struct {
    essvb_log_cb cb;
    void *user;
} essvb_log_ctx;

static void forward_log(ChiakiLogLevel level, const char *message, void *user)
{
    essvb_log_ctx *ctx = (essvb_log_ctx *)user;
    if (ctx && ctx->cb && message)
        ctx->cb(map_log_level(level), message, ctx->user);
}

static int g_init_res = -1;

static void init_once_impl(void)
{
    av_log_set_level(AV_LOG_QUIET);
    g_init_res = (int)chiaki_lib_init();
}

#ifdef _WIN32
static INIT_ONCE g_once = INIT_ONCE_STATIC_INIT;
static BOOL CALLBACK init_once_win(PINIT_ONCE once, PVOID param, PVOID *ctx)
{
    (void)once; (void)param; (void)ctx;
    init_once_impl();
    return TRUE;
}
#else
static pthread_once_t g_once = PTHREAD_ONCE_INIT;
#endif

ESSVB_API int essvb_init(void)
{
#ifdef _WIN32
    InitOnceExecuteOnce(&g_once, init_once_win, NULL, NULL);
#else
    pthread_once(&g_once, init_once_impl);
#endif
    return g_init_res;
}

typedef struct {
    int console_kind;
    essvb_console_cb cb;
    void *user;
} discover_ctx;

static void on_discovery_host(ChiakiDiscoveryHost *host, void *user)
{
    discover_ctx *ctx = (discover_ctx *)user;
    if (!host || !host->host_addr || !ctx->cb)
        return;

    int is_ps5 = chiaki_discovery_host_is_ps5(host) ? 1 : 0;
    if (ctx->console_kind == ESSVB_CONSOLE_PS5 && !is_ps5)
        return;
    if (ctx->console_kind == ESSVB_CONSOLE_PS4_PRO && is_ps5)
        return;

    const char *name = host->host_name ? host->host_name : (is_ps5 ? "PS5" : "PS4 Pro");
    int awake = host->state == CHIAKI_DISCOVERY_HOST_STATE_READY ? 1 : 0;
    ctx->cb(host->host_addr, name, awake, is_ps5, host->running_app_titleid, host->running_app_name, ctx->user);
}

ESSVB_API int essvb_discover(int console_kind, const char *const *targets, int target_count, int timeout_ms,
                             essvb_console_cb callback, void *user)
{
    if (!targets || target_count <= 0 || !callback)
        return 1;

    ChiakiLog log;
    chiaki_log_init(&log, CHIAKI_LOG_ERROR, forward_log, NULL);

    ChiakiDiscovery discovery;
    if (chiaki_discovery_init(&discovery, &log, AF_INET) != CHIAKI_ERR_SUCCESS)
        return -1;

#ifdef _WIN32
    {
        BOOL report = FALSE;
        DWORD returned = 0;
        WSAIoctl(discovery.socket, SIO_UDP_CONNRESET, &report, sizeof(report), NULL, 0, &returned, NULL, NULL);
    }
#endif

    discover_ctx ctx = { console_kind, callback, user };
    ChiakiDiscoveryThread thread;
    if (chiaki_discovery_thread_start(&thread, &discovery, on_discovery_host, &ctx) != CHIAKI_ERR_SUCCESS)
    {
        chiaki_discovery_fini(&discovery);
        return -1;
    }

    uint16_t port = (uint16_t)(console_kind == ESSVB_CONSOLE_PS5 ? CHIAKI_DISCOVERY_PORT_PS5 : CHIAKI_DISCOVERY_PORT_PS4);
    char version_ps5[] = CHIAKI_DISCOVERY_PROTOCOL_VERSION_PS5;
    char version_ps4[] = CHIAKI_DISCOVERY_PROTOCOL_VERSION_PS4;

    struct sockaddr_in *addrs = (struct sockaddr_in *)calloc((size_t)target_count, sizeof(struct sockaddr_in));
    int addr_count = 0;
    if (addrs)
    {
        for (int i = 0; i < target_count; i++)
        {
            if (!targets[i]) continue;
            struct sockaddr_in *addr = &addrs[addr_count];
            addr->sin_family = AF_INET;
            addr->sin_port = htons(port);
            if (inet_pton(AF_INET, targets[i], &addr->sin_addr) == 1)
                addr_count++;
        }
    }

    ChiakiDiscoveryPacket packet;
    memset(&packet, 0, sizeof(packet));
    packet.cmd = CHIAKI_DISCOVERY_CMD_SRCH;
    packet.protocol_version = (console_kind == ESSVB_CONSOLE_PS5) ? version_ps5 : version_ps4;

    uint64_t end = chiaki_time_now_monotonic_ms() + (uint64_t)(timeout_ms > 0 ? timeout_ms : 0);
    do
    {
        for (int i = 0; i < addr_count; i++)
            chiaki_discovery_send(&discovery, &packet, (struct sockaddr *)&addrs[i], sizeof(struct sockaddr_in));
        sleep_ms(450);
    } while (chiaki_time_now_monotonic_ms() < end);

    chiaki_discovery_thread_stop(&thread);
    chiaki_discovery_fini(&discovery);
    free(addrs);
    return addrs ? 0 : -1;
}

ESSVB_API int essvb_wake(int console_kind, const char *host, uint64_t credential)
{
    if (!host) return 1;
    ChiakiLog log;
    chiaki_log_init(&log, CHIAKI_LOG_ERROR, forward_log, NULL);
    bool is_ps5 = (console_kind == ESSVB_CONSOLE_PS5);
    ChiakiErrorCode err = chiaki_discovery_wakeup(&log, NULL, host, credential, is_ps5);
    return err == CHIAKI_ERR_SUCCESS ? 0 : (int)err;
}

typedef struct {
    ChiakiMutex mutex;
    ChiakiCond cond;
    bool done;
    ChiakiRegistEventType type;
    bool has_host;
    ChiakiRegisteredHost host;
    uint32_t reason;
    essvb_log_cb log;
    void *user;
} regist_ctx;

static void on_regist_log(ChiakiLogLevel level, const char *msg, void *user)
{
    regist_ctx *ctx = (regist_ctx *)user;
    if (msg)
    {
        const char *marker = strstr(msg, "Reported Application Reason: 0x");
        if (marker)
        {
            unsigned long val = strtoul(marker + 31, NULL, 16);
            chiaki_mutex_lock(&ctx->mutex);
            ctx->reason = (uint32_t)val;
            chiaki_mutex_unlock(&ctx->mutex);
        }
        if (ctx->log)
            ctx->log(map_log_level(level), msg, ctx->user);
    }
}

static void on_regist_event(ChiakiRegistEvent *ev, void *user)
{
    regist_ctx *ctx = (regist_ctx *)user;
    chiaki_mutex_lock(&ctx->mutex);
    if (!ctx->done)
    {
        ctx->type = ev ? ev->type : CHIAKI_REGIST_EVENT_TYPE_FINISHED_FAILED;
        if (ev && ev->type == CHIAKI_REGIST_EVENT_TYPE_FINISHED_SUCCESS && ev->registered_host)
        {
            ctx->host = *ev->registered_host;
            ctx->has_host = true;
        }
        ctx->done = true;
    }
    chiaki_mutex_unlock(&ctx->mutex);
    chiaki_cond_signal(&ctx->cond);
}

static bool regist_done_pred(void *user) { return ((regist_ctx *)user)->done; }

ESSVB_API int essvb_register(int console_kind, const char *host, const uint8_t account_id[8], uint32_t pin,
                             essvb_registered_console *out, uint32_t *reason, essvb_log_cb log, void *user)
{
    if (reason) *reason = 0;
    if (!host || !account_id || !out) return -1;

    regist_ctx *ctx = (regist_ctx *)calloc(1, sizeof(regist_ctx));
    if (!ctx) return -1;
    ctx->log = log;
    ctx->user = user;

    if (chiaki_mutex_init(&ctx->mutex, false) != CHIAKI_ERR_SUCCESS ||
        chiaki_cond_init(&ctx->cond) != CHIAKI_ERR_SUCCESS)
    {
        free(ctx);
        return -1;
    }

    ChiakiLog rlog;
    chiaki_log_init(&rlog, CHIAKI_LOG_ERROR | CHIAKI_LOG_WARNING | CHIAKI_LOG_INFO, on_regist_log, ctx);

    ChiakiRegistInfo info;
    memset(&info, 0, sizeof(info));
    info.target = resolve_target(console_kind);
    info.host = host;
    info.broadcast = false;
    memcpy(info.psn_account_id, account_id, CHIAKI_PSN_ACCOUNT_ID_SIZE);
    info.pin = pin;

    ChiakiRegist regist;
    int result = 1;
    if (chiaki_regist_start(&regist, &rlog, &info, on_regist_event, ctx) != CHIAKI_ERR_SUCCESS)
    {
        result = -1;
    }
    else
    {
        chiaki_mutex_lock(&ctx->mutex);
        ChiakiErrorCode err = chiaki_cond_timedwait_pred(&ctx->cond, &ctx->mutex, 30000, regist_done_pred, ctx);
        if (err == CHIAKI_ERR_TIMEOUT)
        {
            chiaki_mutex_unlock(&ctx->mutex);
            chiaki_regist_stop(&regist);
            chiaki_mutex_lock(&ctx->mutex);
            chiaki_cond_wait_pred(&ctx->cond, &ctx->mutex, regist_done_pred, ctx);
        }
        chiaki_mutex_unlock(&ctx->mutex);
        chiaki_regist_fini(&regist);

        if (ctx->type == CHIAKI_REGIST_EVENT_TYPE_FINISHED_SUCCESS && ctx->has_host)
        {
            memset(out, 0, sizeof(*out));
            memcpy(out->nickname, ctx->host.server_nickname, sizeof(out->nickname));
            out->nickname[sizeof(out->nickname) - 1] = '\0';
            memcpy(out->regist_key, ctx->host.rp_regist_key, sizeof(out->regist_key));
            memcpy(out->morning, ctx->host.rp_key, sizeof(out->morning));
            result = 0;
        }
        else if (ctx->type == CHIAKI_REGIST_EVENT_TYPE_FINISHED_CANCELED)
        {
            result = 2;
        }
        else
        {
            if (reason) *reason = ctx->reason;
            result = 1;
        }
    }

    chiaki_cond_fini(&ctx->cond);
    chiaki_mutex_fini(&ctx->mutex);
    free(ctx);
    return result;
}

typedef struct {
    AVCodecContext *codec;
    AVPacket *packet;
    AVFrame *frame;
    struct SwsContext *sws;
    uint8_t *bgra;
    int width;
    int height;
    int stride;
} essvb_decoder;

static int decoder_init(essvb_decoder *dec)
{
    memset(dec, 0, sizeof(*dec));
    const AVCodec *codec = avcodec_find_decoder(AV_CODEC_ID_H264);
    if (!codec) return -1;
    dec->codec = avcodec_alloc_context3(codec);
    dec->packet = av_packet_alloc();
    dec->frame = av_frame_alloc();
    if (!dec->codec || !dec->packet || !dec->frame) goto fail;
    dec->codec->flags |= AV_CODEC_FLAG_LOW_DELAY;
    dec->codec->thread_type = FF_THREAD_SLICE;
    if (avcodec_open2(dec->codec, codec, NULL) < 0) goto fail;
    return 0;
fail:
    avcodec_free_context(&dec->codec);
    av_packet_free(&dec->packet);
    av_frame_free(&dec->frame);
    return -1;
}

static void decoder_fini(essvb_decoder *dec)
{
    avcodec_free_context(&dec->codec);
    av_packet_free(&dec->packet);
    av_frame_free(&dec->frame);
    sws_freeContext(dec->sws);
    av_freep(&dec->bgra);
}

static bool decoder_to_bgra(essvb_decoder *dec, AVFrame *src)
{
    int w = src->width, h = src->height;
    if (w <= 0 || h <= 0) return false;
    if (w != dec->width || h != dec->height || !dec->bgra)
    {
        av_freep(&dec->bgra);
        dec->stride = FFALIGN(w * 4, 64);
        dec->bgra = (uint8_t *)av_malloc((size_t)dec->stride * (size_t)h);
        if (!dec->bgra) return false;
        dec->width = w;
        dec->height = h;
    }

    dec->sws = sws_getCachedContext(dec->sws, w, h, (enum AVPixelFormat)src->format,
                                    w, h, AV_PIX_FMT_BGRA, SWS_POINT, NULL, NULL, NULL);
    if (!dec->sws) return false;

    uint8_t *dst[4] = { dec->bgra, NULL, NULL, NULL };
    int dst_stride[4] = { dec->stride, 0, 0, 0 };
    return sws_scale(dec->sws, (const uint8_t *const *)src->data, src->linesize, 0, h, dst, dst_stride) == h;
}

struct essvb_session {
    ChiakiSession session;
    ChiakiLog log;
    essvb_log_ctx log_ctx;
    essvb_frame_cb frame_cb;
    essvb_event_cb event_cb;
    void *user;
    essvb_decoder decoder;
    uint64_t min_interval_us;
    uint64_t last_frame_us;
};

static int map_quit_reason(ChiakiQuitReason r)
{
    switch (r)
    {
        case CHIAKI_QUIT_REASON_STOPPED: return ESSVB_QUIT_STOPPED;
        case CHIAKI_QUIT_REASON_SESSION_REQUEST_RP_IN_USE: return ESSVB_QUIT_IN_USE;
        case CHIAKI_QUIT_REASON_SESSION_REQUEST_CONNECTION_REFUSED:
        case CHIAKI_QUIT_REASON_CTRL_CONNECTION_REFUSED: return ESSVB_QUIT_REFUSED;
        case CHIAKI_QUIT_REASON_PSN_REGIST_FAILED: return ESSVB_QUIT_PSN_REGIST_FAILED;
        case CHIAKI_QUIT_REASON_STREAM_CONNECTION_REMOTE_DISCONNECTED:
        case CHIAKI_QUIT_REASON_STREAM_CONNECTION_REMOTE_SHUTDOWN: return ESSVB_QUIT_CONSOLE_CLOSED;
        case CHIAKI_QUIT_REASON_CTRL_CONNECT_FAILED:
        case CHIAKI_QUIT_REASON_STREAM_CONNECTION_UNKNOWN: return ESSVB_QUIT_NETWORK;
        case CHIAKI_QUIT_REASON_SESSION_REQUEST_RP_CRASH: return ESSVB_QUIT_RP_CRASHED;
        default: return ESSVB_QUIT_OTHER;
    }
}

static void on_session_event(ChiakiEvent *ev, void *user)
{
    essvb_session *s = (essvb_session *)user;
    if (!ev || !s->event_cb) return;
    switch (ev->type)
    {
        case CHIAKI_EVENT_CONNECTED:
            s->event_cb(ESSVB_EVENT_CONNECTED, 0, NULL, s->user);
            break;
        case CHIAKI_EVENT_LOGIN_PIN_REQUEST:
            s->event_cb(ESSVB_EVENT_LOGIN_PIN, ev->login_pin_request.pin_incorrect ? 1 : 0, NULL, s->user);
            break;
        case CHIAKI_EVENT_QUIT:
        {
            int reason = map_quit_reason(ev->quit.reason);
            const char *txt = reason == ESSVB_QUIT_OTHER ? chiaki_quit_reason_string(ev->quit.reason) : ev->quit.reason_str;
            s->event_cb(ESSVB_EVENT_QUIT, reason, txt, s->user);
            break;
        }
        default:
            break;
    }
}

static bool on_video_sample(uint8_t *buf, size_t size, int32_t lost, bool recovered, void *user)
{
    (void)lost; (void)recovered;
    essvb_session *s = (essvb_session *)user;
    if (!buf || !size) return true;

    s->decoder.packet->data = buf;
    s->decoder.packet->size = (int)size;
    int r = avcodec_send_packet(s->decoder.codec, s->decoder.packet);
    av_packet_unref(s->decoder.packet);
    if (r < 0) return false;

    bool got = false;
    while (avcodec_receive_frame(s->decoder.codec, s->decoder.frame) == 0)
        got = true;

    if (!got || !s->frame_cb) return true;

    uint64_t now = chiaki_time_now_monotonic_us();
    if (s->min_interval_us > 0 && s->last_frame_us > 0 && (now - s->last_frame_us) < s->min_interval_us)
    {
        av_frame_unref(s->decoder.frame);
        return true;
    }
    s->last_frame_us = now;

    if (decoder_to_bgra(&s->decoder, s->decoder.frame))
        s->frame_cb(s->decoder.bgra, s->decoder.width, s->decoder.height, s->decoder.stride, s->user);

    av_frame_unref(s->decoder.frame);
    return true;
}

static void on_cant_display(void *user, bool cant_display)
{
    essvb_session *s = (essvb_session *)user;
    CHIAKI_LOGI(&s->log, "PlayStation stream %s", cant_display ? "protected content" : "displayable");
}

ESSVB_API essvb_session *essvb_session_start(int console_kind, const char *host, const uint8_t regist_key[0x10],
                                             const uint8_t morning[0x10], const uint8_t account_id[8],
                                             int max_fps, int video_height,
                                             essvb_frame_cb frame, essvb_event_cb event, essvb_log_cb log, void *user)
{
    if (!host || !regist_key || !morning || !account_id) return NULL;
    essvb_session *s = (essvb_session *)calloc(1, sizeof(essvb_session));
    if (!s) return NULL;

    s->frame_cb = frame;
    s->event_cb = event;
    s->user = user;
    s->log_ctx.cb = log;
    s->log_ctx.user = user;
    s->min_interval_us = max_fps > 0 ? 1000000ULL / (uint64_t)max_fps : 0;
    chiaki_log_init(&s->log, CHIAKI_LOG_ERROR | CHIAKI_LOG_WARNING | CHIAKI_LOG_INFO, forward_log, &s->log_ctx);

    if (decoder_init(&s->decoder) != 0)
    {
        free(s);
        return NULL;
    }

    ChiakiConnectInfo info;
    memset(&info, 0, sizeof(info));
    info.ps5 = (console_kind == ESSVB_CONSOLE_PS5);
    info.host = host;
    memcpy(info.regist_key, regist_key, sizeof(info.regist_key));
    memcpy(info.morning, morning, sizeof(info.morning));
    chiaki_connect_video_profile_preset(
        &info.video_profile,
        video_height == 720 ? CHIAKI_VIDEO_RESOLUTION_PRESET_720p : CHIAKI_VIDEO_RESOLUTION_PRESET_1080p,
        CHIAKI_VIDEO_FPS_PRESET_30);
    info.video_profile.codec = CHIAKI_CODEC_H264;
    info.video_profile_auto_downgrade = true;
    info.enable_keyboard = false;
    info.enable_dualsense = false;
    info.audio_video_disabled = CHIAKI_AUDIO_DISABLED;
    memcpy(info.psn_account_id, account_id, CHIAKI_PSN_ACCOUNT_ID_SIZE);
    info.packet_loss_max = 0.05;
    info.enable_idr_on_fec_failure = true;

    if (chiaki_session_init(&s->session, &info, &s->log) != CHIAKI_ERR_SUCCESS)
    {
        decoder_fini(&s->decoder);
        free(s);
        return NULL;
    }

    chiaki_session_set_event_cb(&s->session, on_session_event, s);
    chiaki_session_set_video_sample_cb(&s->session, on_video_sample, s);
    ChiakiCtrlDisplaySink sink = { .user = s, .cantdisplay_cb = on_cant_display };
    chiaki_session_ctrl_set_display_sink(&s->session, &sink);

    if (chiaki_session_start(&s->session) != CHIAKI_ERR_SUCCESS)
    {
        chiaki_session_fini(&s->session);
        decoder_fini(&s->decoder);
        free(s);
        return NULL;
    }
    return s;
}

ESSVB_API int essvb_session_set_login_pin(essvb_session *session, const uint8_t *pin, size_t pin_size)
{
    if (!session || !pin || !pin_size) return CHIAKI_ERR_INVALID_DATA;
    return chiaki_session_set_login_pin(&session->session, pin, pin_size);
}

ESSVB_API void essvb_session_stop(essvb_session *session)
{
    if (!session) return;
    chiaki_session_stop(&session->session);
    chiaki_session_join(&session->session);
    chiaki_session_fini(&session->session);
    decoder_fini(&session->decoder);
    free(session);
}
