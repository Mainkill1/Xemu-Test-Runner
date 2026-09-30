// SPDX-License-Identifier: MIT
#pragma once
#include "pad-state.h"
#include <chrono>

namespace xtr {
inline std::chrono::steady_clock::time_point freshness_deadline(
    const PadState& state, std::chrono::steady_clock::time_point now) {
    // Losing the supervisor while all controls are released cannot leave
    // gameplay input held. Keep the neutral device connected through a host
    // scheduling stall; the 250 ms safety deadline still applies to any
    // button, trigger, or stick state that could affect the game.
    if (state.buttons == 0 && state.lt == 0 && state.rt == 0 &&
        state.lx == 0 && state.ly == 0 && state.rx == 0 && state.ry == 0)
        return std::chrono::steady_clock::time_point::max();
    return now + std::chrono::milliseconds(250);
}
}
