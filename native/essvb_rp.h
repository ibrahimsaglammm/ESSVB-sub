/* Copyright (c) 2026 ESSVB. All Rights Reserved. */
/* Native C ABI bridge over libchiaki + FFmpeg for PS5 & PS4 Pro Remote Play. */

#ifndef ESSVB_RP_H
#define ESSVB_RP_H

#include <stddef.h>
#include <stdint.h>

#ifdef _WIN32
#define ESSVB_API __declspec(dllexport)
#else
#define ESSVB_API __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

enum {
    ESSVB_CONSOLE_PS5 = 0,
    ESSVB_CONSOLE_PS4_PRO = 1
};

enum {
    ESSVB_EVENT_CONNECTED = 0,
    ESSVB_EVENT_QUIT = 1,
    ESSVB_EVENT_LOGIN_PIN = 2
};

enum {
    ESSVB_QUIT_STOPPED = 0,
    ESSVB_QUIT_IN_USE = 1,
    ESSVB_QUIT_REFUSED = 2,
    ESSVB_QUIT_NETWORK = 3,
    ESSVB_QUIT_OTHER = 4,
    ESSVB_QUIT_RP_CRASHED = 5,
    ESSVB_QUIT_PSN_REGIST_FAILED = 6,
    ESSVB_QUIT_CONSOLE_CLOSED = 7
};

typedef struct {
    char nickname[0x20];
    uint8_t regist_key[0x10];
    uint8_t morning[0x10];
} essvb_registered_console;

typedef struct essvb_session essvb_session;

typedef void (*essvb_log_cb)(int level, const char *message, void *user);
typedef void (*essvb_console_cb)(const char *host, const char *name, int awake, int is_ps5, const char *title_id, const char *app_name, void *user);
typedef void (*essvb_frame_cb)(const uint8_t *bgra, int width, int height, int stride, void *user);
typedef void (*essvb_event_cb)(int event, int quit_reason, const char *reason_text, void *user);

ESSVB_API int essvb_init(void);

ESSVB_API int essvb_discover(int console_kind, const char *const *targets, int target_count, int timeout_ms,
                             essvb_console_cb callback, void *user);

ESSVB_API int essvb_wake(int console_kind, const char *host, uint64_t credential);

ESSVB_API int essvb_register(int console_kind, const char *host, const uint8_t account_id[8], uint32_t pin,
                             essvb_registered_console *out, uint32_t *reason, essvb_log_cb log, void *user);

ESSVB_API essvb_session *essvb_session_start(int console_kind, const char *host, const uint8_t regist_key[0x10],
                                             const uint8_t morning[0x10], const uint8_t account_id[8],
                                             int max_fps, int video_height,
                                             essvb_frame_cb frame, essvb_event_cb event, essvb_log_cb log, void *user);

ESSVB_API int essvb_session_set_login_pin(essvb_session *session, const uint8_t *pin, size_t pin_size);
ESSVB_API void essvb_session_stop(essvb_session *session);

#ifdef __cplusplus
}
#endif

#endif
