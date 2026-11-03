#pragma once
// Public source builds keep the compositor enabled without shipping game-derived tables.
namespace pawn_dance_native {
inline void init() { reshade::log::message(reshade::log::level::info,
    "MC-CS-Bridge: experimental native F8 dance is not built; compositor remains available."); }
inline void shutdown() {}
inline void begin() {}
inline bool local_preview_enabled() { return false; }
inline void set_local_preview(bool enabled) {
    if(enabled)reshade::log::message(reshade::log::level::info,
        "MC-CS-Bridge: F8 unavailable in this build. Generate local tables/cache and explicitly enable MCCS_ENABLE_EXPERIMENTAL_DANCE.");
}
}
