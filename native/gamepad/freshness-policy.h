// SPDX-License-Identifier: MIT
#pragma once
#include "pad-state.h"
#include <chrono>

namespace xtr {
inline std::chrono::steady_clock::time_point freshness_deadline(
    const PadState& state, std::chrono::steady_clock::time_point now) {
    // A neutral state cannot leave gameplay input held, so it needs no
    // freshness deadline. Non-neutral state gets 250 ms; expiry causes the
    // worker to apply neutral while retaining controller ownership until the
    // supervisor pipe actually ends.
    if (state.buttons == 0 && state.lt == 0 && state.rt == 0 &&
        state.lx == 0 && state.ly == 0 && state.rx == 0 && state.ry == 0)
        return std::chrono::steady_clock::time_point::max();
    return now + std::chrono::milliseconds(250);
}
}
