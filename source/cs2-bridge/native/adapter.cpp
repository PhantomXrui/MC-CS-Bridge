#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <shellapi.h>
#include <cstdint>
#include <cstring>
#include <reshade.hpp>
#include "compositor.h"
#include "depth_capture.h"
#include "ui_input.h"
#include "pose_smoothing.h"
#include "shader_version_compat.h"
// Synchronous GPU capture is deliberately absent from production builds.
// It must not be enabled by a leftover sentinel or shared-memory request.
#ifdef MCCS_ENABLE_EXPERIMENTAL_DANCE
#include "pawn_dance_native.h"
#else
#include "pawn_dance_disabled.h"
#endif

extern "C" __declspec(dllexport) const char *NAME = "Minecraft CS2 local passthrough";
extern "C" __declspec(dllexport) const char *DESCRIPTION = "Real Minecraft frames in a local insecure CS2 bot match. No network transport inside CS2.";
namespace {
HANDLE mapping=nullptr;
uint8_t *memory=nullptr;
uint64_t lastTry=0,activeSince=0;
bool screenshotTaken=false;
bool timingOnly=false,configuredEffects=false;
bool modeKeyHeld=false;
bool cameraKeyHeld=false;
bool danceKeyHeld=false;
uint32_t pendingDanceTaps=0;
uint32_t captureRequest=0;
int cursorIncrements=0;
// Bytes 160..163 are the pointer sequence; 164..167 belong to focus.
// A 64-bit write here silently cleared focus whenever the inventory opened.
uint32_t pointerSerial=0;
double qpc_ms(){static LARGE_INTEGER f{};if(!f.QuadPart)QueryPerformanceFrequency(&f);LARGE_INTEGER t;QueryPerformanceCounter(&t);return double(t.QuadPart)*1000.0/f.QuadPart;}
float prepareMs=0,uploadMs=0,effectsMs=0;
double effectsStart=0;
PoseSmoothing poseSmoothing;
uint64_t renderPoseSequence=0;
template<typename T> T get(size_t offset) {T value;std::memcpy(&value,memory+offset,sizeof value);return value;}
template<typename T> void put(size_t offset,T value) {std::memcpy(memory+offset,&value,sizeof value);}
// Bounded in-memory diagnostics: never write recurring logs on the render thread.
// 1888..1935 contain max ms, >25ms counts and latest ms for four prepare phases.
struct PreparePhases {
    double stamp=qpc_ms();
    void mark(unsigned phase) {
        const double now=qpc_ms();const float ms=float(now-stamp);stamp=now;
        if(!memory)return;
        const size_t offset=phase*4;
        put<float>(1888+offset,(std::max)(get<float>(1888+offset),ms));
        if(ms>25)put<uint32_t>(1904+offset,get<uint32_t>(1904+offset)+1);
        put<float>(1920+offset,ms);
    }
};
bool open_map() {
    if(memory)return true;
    if(GetTickCount64()-lastTry<1000)return false;
    lastTry=GetTickCount64();mapping=OpenFileMappingW(FILE_MAP_READ|FILE_MAP_WRITE,FALSE,L"Local\\MC_CS_Bridge_Pose");
    if(!mapping)return false;
    memory=static_cast<uint8_t *>(MapViewOfFile(mapping,FILE_MAP_READ|FILE_MAP_WRITE,0,0,4096));
    if(!memory){CloseHandle(mapping);mapping=nullptr;return false;}
    reshade::log::message(reshade::log::level::info,"CS2 bridge: shared camera connected");return true;
}
bool spectator_dance_session() {
    // Explicit offline-session opt-in: no assumptions about engine vtables or
    // server-side player controllers, which are absent during demo playback.
    static const bool enabled=[]{
        int count=0;LPWSTR *args=CommandLineToArgvW(GetCommandLineW(),&count);
        bool spectator=false,insecure=false,lan=false;
        if(args){for(int i=1;i<count;i++){
            spectator|=_wcsicmp(args[i],L"-mccs-spectator-dance")==0;
            insecure|=_wcsicmp(args[i],L"-insecure")==0;
            lan|=_wcsicmp(args[i],L"+sv_lan")==0&&i+1<count&&_wcsicmp(args[i+1],L"1")==0;
        }LocalFree(args);}
        const bool local=insecure&&(spectator||lan);
        #ifdef MCCS_ENABLE_EXPERIMENTAL_DANCE
        if(local)reshade::log::message(reshade::log::level::info,"CS2 dance: offline lobby/replay ready; F8 toggles original-model dance");
#endif
        return local;
    }();
    return enabled;
}
void poll_dance_key(reshade::api::effect_runtime *runtime) {
    DWORD foreground=0;GetWindowThreadProcessId(GetForegroundWindow(),&foreground);
    const bool focused=foreground==GetCurrentProcessId();
    const bool tapped=focused&&(runtime->is_key_pressed(VK_F8)||
        (!danceKeyHeld&&runtime->is_key_released(VK_F8)));
    danceKeyHeld=focused&&runtime->is_key_down(VK_F8);
    if(!focused)pendingDanceTaps=0;
    else if(tapped)++pendingDanceTaps;
    bool bridgeActive=false,bridgeUi=false;
    if(memory&&get<uint32_t>(0)==0x504D5343&&get<int32_t>(4)==1){
        const uint64_t seq=get<uint64_t>(8);
        // A stopped publisher can leave a stale odd sequence. Only defer while
        // a fresh bridge update is in flight, retaining short taps for retry.
        if(GetTickCount64()-get<uint64_t>(16)<1000){
            if(seq&1)return;
            bridgeActive=get<int32_t>(24)!=0;
            bridgeUi=get<int32_t>(28)!=0&&get<int32_t>(136)!=0;
            MemoryBarrier();if(seq!=get<uint64_t>(8))return;
        }
    }
    // One tap has exactly one owner. The normal MC bridge keeps its server-side
    // request counter; a standalone replay owns its local rendering toggle.
    if(bridgeActive){
        pawn_dance_native::set_local_preview(false);
        if(pendingDanceTaps&&!bridgeUi)put<uint32_t>(2000,get<uint32_t>(2000)+pendingDanceTaps);
    }else if(spectator_dance_session()&&(pendingDanceTaps&1)){
        pawn_dance_native::set_local_preview(!pawn_dance_native::local_preview_enabled());
    }
    pendingDanceTaps=0;
}
void before_effects(reshade::api::effect_runtime *runtime,reshade::api::command_list *cmd,reshade::api::resource_view,reshade::api::resource_view) {
    const double start=qpc_ms();
    // Runs even for early exits, so McActive never retains stale state.
    struct Finish { reshade::api::effect_runtime *r;double start;~Finish(){prepareMs=float(qpc_ms()-start);double t=qpc_ms();compositor::prepare_frame(r);uploadMs=float(qpc_ms()-t);effectsStart=qpc_ms();} } finish{runtime,start};
    PreparePhases phases;
    csdepth::begin(runtime,cmd);phases.mark(0);
    open_map();
    if(!open_map() || get<uint32_t>(0)!=0x504D5343 || get<int32_t>(4)!=1){compositor::set_active(false);return;}
    uint64_t seq=get<uint64_t>(8);if(seq&1)return;
    const uint64_t stamp=get<uint64_t>(16);float p[9];std::memcpy(p,memory+32,sizeof p);
    const auto poseClock=get<int64_t>(208),poseFrequency=get<int64_t>(216);
    const bool active=get<int32_t>(24)!=0 && GetTickCount64()-stamp<1000;
    const float lookYaw=get<float>(1968),lookPitch=get<float>(1972);
    const int cameraMode=get<int32_t>(1976);
    MemoryBarrier();if(seq!=get<uint64_t>(8))return;
    if(active && get<int32_t>(200)!=0 && poseClock>0 && poseFrequency>0){poseSmoothing.sample(seq,double(poseClock)*1000.0/poseFrequency,p);poseSmoothing.at(qpc_ms(),p);}
    else poseSmoothing.reset();
    // 1792..1883: render pose, after the 64x24-byte input ring and before diagnostics.
    // Minecraft reads one coherent snapshot directly instead of waiting for JSON/pipe/WS.
    put<uint64_t>(1792,++renderPoseSequence);MemoryBarrier();
    put<uint32_t>(1800,0x50525343);put<int32_t>(1804,active?1:0);
    put<uint64_t>(1808,seq/2-1);std::memcpy(memory+1816,p,sizeof p);
    put<float>(1852,lookYaw);put<float>(1856,lookPitch);put<int32_t>(1860,cameraMode);
    MemoryBarrier();put<uint64_t>(1792,++renderPoseSequence);
    uint32_t width=0,height=0;runtime->get_screenshot_width_and_height(&width,&height);
    put<uint32_t>(80,width);put<uint32_t>(84,height);
    put<uint64_t>(112,GetTickCount64());
    phases.mark(1);
    DWORD foreground=0;GetWindowThreadProcessId(GetForegroundWindow(),&foreground);
    bool focused=foreground==GetCurrentProcessId();
    put<int32_t>(164,focused?1:0);
    if(focused){auto pressed=runtime->last_key_pressed(),released=runtime->last_key_released();if(pressed)put<uint32_t>(224,pressed);if(released)put<uint32_t>(228,released);}
    bool build=get<int32_t>(28)!=0;
    const bool modeDown=focused && runtime->is_key_down(VK_F7);
    if(focused && (runtime->is_key_pressed(VK_F7)||(!modeKeyHeld&&runtime->is_key_released(VK_F7)))){build=!build;put<int32_t>(28,build?1:0);}
    modeKeyHeld=modeDown;
    uint64_t keys=0;
    bool ui=active && focused && build && get<int32_t>(136)!=0;
    const bool cameraDown=focused && runtime->is_key_down(VK_F5);
    if(active && focused && build && !ui &&
       (runtime->is_key_pressed(VK_F5)||(!cameraKeyHeld&&runtime->is_key_released(VK_F5))))
        put<int32_t>(232,(get<int32_t>(232)+1)%3);
    cameraKeyHeld=cameraDown;
    ui_input::poll(runtime,memory,ui);
    ui_input::poll_gameplay(runtime,memory,active && focused && build && !ui);
    ui_input::poll_movement(runtime,memory,active && focused && get<int32_t>(1980)!=0 && !ui);
    if(ui){
        runtime->block_input_next_frame();ClipCursor(nullptr);
        if(cursorIncrements==0){do{++cursorIncrements;}while(ShowCursor(TRUE)<0 && cursorIncrements<16);}
        uint32_t x=0,y=0;int16_t wheel=0;runtime->get_mouse_cursor_position(&x,&y,&wheel);
        // ReShade 6.8 input_windows.cpp already converts MSG.pt to client coordinates.
        // Applying ScreenToClient again offsets every inventory click by the window origin.
        POINT point{static_cast<LONG>(x),static_cast<LONG>(y)};
        RECT bounds{};GetClientRect(GetForegroundWindow(),&bounds);
        put<float>(140,float(point.x)/float(bounds.right?bounds.right:width));put<float>(144,float(point.y)/float(bounds.bottom?bounds.bottom:height));
        put<int32_t>(148,(runtime->is_key_down(VK_LBUTTON)?1:0)|(runtime->is_key_down(VK_RBUTTON)?2:0));
        put<int32_t>(152,wheel>0?1:wheel<0?-1:0);put<int32_t>(156,runtime->is_key_down(VK_SHIFT)?1:0);put<uint32_t>(160,++pointerSerial);
        ui_input::pointer(runtime,memory,get<float>(140),get<float>(144),wheel>0?1:wheel<0?-1:0,runtime->is_key_down(VK_SHIFT)?1:0);
    }else{ui_input::pointerHeld=0;if(cursorIncrements){while(cursorIncrements){ShowCursor(FALSE);--cursorIncrements;}}}
    if(active && focused && build){
        if(!ui && runtime->is_key_down(VK_LBUTTON))keys|=1;
        if(!ui && runtime->is_key_down(VK_RBUTTON))keys|=2;
        if(!ui && runtime->is_key_down('E')){keys|=4;runtime->block_input_next_frame();}
        if(!ui && runtime->is_key_down('Q'))keys|=8;
        if(!ui && runtime->is_key_down('F'))keys|=16;
        for(int i=0;i<9;i++)if(!ui && (runtime->is_key_pressed('1'+i)||runtime->is_key_released('1'+i)))put<int32_t>(104,i);
        uint32_t mx,my;int16_t wheel=0;runtime->get_mouse_cursor_position(&mx,&my,&wheel);
        if(!ui && wheel){int slot=get<int32_t>(104);slot=(slot-(wheel>0?1:-1)+9)%9;put<int32_t>(104,slot);}
    }
    put<uint64_t>(96,keys);phases.mark(2);
    compositor::set_active(active);
    runtime->set_uniform_value_bool(runtime->find_uniform_variable("MCPassthrough.fx","ShowGuestOverlay"),build);
    compositor::set_host_pose(p[3],p[4],0,p[5],p[0],p[1],p[2]);
    // Initial Source 2 depth calibration, in MC blocks (40 Source units/block).
    float nearPlane=get<float>(128);if(nearPlane<=0)nearPlane=6.5f/40.0f;
    compositor::set_host_planes(nearPlane,16384.0f/40.0f);
    runtime->set_uniform_value_int(runtime->find_uniform_variable("MCPassthrough.fx","DebugView"),get<int32_t>(124));
    runtime->set_uniform_value_bool(runtime->find_uniform_variable("MCPassthrough.fx","HostReversedZ"),get<int32_t>(132)==2);
    compositor::set_pose_lag(0);compositor::set_look(0.15f,0.04f,0.0f);
    if(active && build){if(!activeSince)activeSince=GetTickCount64();}
    else activeSince=0;
    phases.mark(3);
}
void after_effects(reshade::api::effect_runtime *runtime,reshade::api::command_list *,reshade::api::resource_view,reshade::api::resource_view){compositor::finish_frame(runtime);effectsMs=float(qpc_ms()-effectsStart);}
void after_present(reshade::api::effect_runtime *runtime) {
    open_map();
    // Input and the next scene's dance state must also advance when ReShade
    // effects are disabled. begin_effects is not called in that case.
    poll_dance_key(runtime);
    pawn_dance_native::begin();
    if(!configuredEffects){runtime->set_effects_state(!timingOnly);configuredEffects=true;}
    static LARGE_INTEGER frequency{},last{};LARGE_INTEGER now{};
    if(!frequency.QuadPart)QueryPerformanceFrequency(&frequency);
    QueryPerformanceCounter(&now);
    if(memory && last.QuadPart){auto count=get<uint64_t>(192);put<float>(2048+(count%128)*4,float(1000.0*(now.QuadPart-last.QuadPart)/frequency.QuadPart));put<float>(2560+(count%128)*12,prepareMs);put<float>(2564+(count%128)*12,uploadMs);put<float>(2568+(count%128)*12,effectsMs);MemoryBarrier();put<uint64_t>(192,count+1);}
    last=now;
    csdepth::end();
    if(memory && get<uint32_t>(120)!=captureRequest){captureRequest=get<uint32_t>(120);runtime->save_screenshot("MC-CS-Bridge-check");csdepth::dump();}
    if(runtime->is_key_pressed(VK_F10))runtime->save_screenshot("MC-CS-Bridge-manual");
}
}
BOOL APIENTRY DllMain(HMODULE module,DWORD reason,LPVOID) {
    if(reason==DLL_PROCESS_ATTACH){
        DisableThreadLibraryCalls(module);
        timingOnly=GetFileAttributesW(L"D:\\MC-CS-Bridge\\runtime\\timing-only.flag")!=INVALID_FILE_ATTRIBUTES;
        if(timingOnly){if(!reshade::register_addon(module))return FALSE;reshade::register_event<reshade::addon_event::reshade_present>(after_present);return TRUE;}
        if(!compositor::try_register(module))return FALSE;
        csdepth::register_events();
        pawn_dance_native::init();
        reshade::register_event<reshade::addon_event::reshade_begin_effects>(before_effects);
        reshade::register_event<reshade::addon_event::reshade_finish_effects>(after_effects);
        reshade::register_event<reshade::addon_event::reshade_present>(after_present);
    }else if(reason==DLL_PROCESS_DETACH){
        pawn_dance_native::shutdown();
        if(timingOnly)reshade::unregister_addon(module);else compositor::unregister(module);
        if(memory)UnmapViewOfFile(memory);if(mapping)CloseHandle(mapping);
    }
    return TRUE;
}
