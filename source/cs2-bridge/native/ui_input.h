#pragma once
#include <array>
#include <bit>

// The producer and consumer each own one monotonic counter. Events are only
// collected while the game is focused and Minecraft's GUI is visible.
namespace ui_input {
std::array<bool,256> held{};
std::array<ULONGLONG,256> repeatAt{};
bool wasActive=false;
int scan(int vk) {
    if(vk>='A'&&vk<='Z')return 4+vk-'A';
    if(vk>='1'&&vk<='9')return 30+vk-'1';
    if(vk=='0')return 39;
    switch(vk){
    case VK_RETURN:return 40;case VK_ESCAPE:return 41;case VK_BACK:return 42;
    case VK_TAB:return 43;case VK_SPACE:return 44;case VK_OEM_MINUS:return 45;
    case VK_OEM_PLUS:return 46;case VK_OEM_4:return 47;case VK_OEM_6:return 48;
    case VK_OEM_5:return 49;case VK_OEM_1:return 51;case VK_OEM_7:return 52;
    case VK_OEM_3:return 53;case VK_OEM_COMMA:return 54;case VK_OEM_PERIOD:return 55;
    case VK_OEM_2:return 56;case VK_HOME:return 74;case VK_PRIOR:return 75;
    case VK_DELETE:return 76;case VK_END:return 77;case VK_NEXT:return 78;
    case VK_RIGHT:return 79;case VK_LEFT:return 80;case VK_DOWN:return 81;case VK_UP:return 82;
    default:return 0;
    }
}
void push(uint8_t* mem,int type,int key,int code,int mods,int down,int extra=0) {
    auto* write=reinterpret_cast<volatile LONG64*>(mem+176);
    const auto w=*write;
    auto* read=reinterpret_cast<volatile LONG64*>(mem+184);
    if(w-*read>=64)return; // No overwrite of an event the server is reading.
    const int event[6]={type,key,code,mods,down,extra};
    std::memcpy(mem+256+(w%64)*24,event,sizeof event);MemoryBarrier();
    InterlockedExchange64(write,w+1);
}
// Keep short clicks: the 64 Hz server must not have to observe a transient
// button bit from a faster render frame. Down/up remain ordered in the ring.
std::array<bool,5> gameplayHeld{};
std::array<bool,7> movementHeld{};
void poll_movement(reshade::api::effect_runtime* runtime,uint8_t* mem,bool active) {
    constexpr int movementKeys[7]={'W','S','A','D',VK_SPACE,VK_SHIFT,VK_CONTROL};
    uint64_t mask=0;
    for(int i=0;i<7;i++){
        const int vk=movementKeys[i];
        const bool down=active&&runtime->is_key_down(vk);
        const bool pressed=active&&(runtime->is_key_pressed(vk)||(!movementHeld[i]&&runtime->is_key_released(vk)));
        if(pressed||(down&&!movementHeld[i]))push(mem,5,i,0,0,1);
        if(!down&&(movementHeld[i]||pressed))push(mem,5,i,0,0,0);
        if(down)mask|=uint64_t(1)<<i;
        movementHeld[i]=down;
    }
    std::memcpy(mem+1984,&mask,sizeof mask);
    const uint32_t version=1;std::memcpy(mem+1992,&version,sizeof version);
}
int pointerHeld=0;
void pointer(reshade::api::effect_runtime* runtime,uint8_t* mem,float x,float y,int wheel,int shift) {
    const int down=(runtime->is_key_down(VK_LBUTTON)?1:0)|(runtime->is_key_down(VK_RBUTTON)?2:0);
    const int tap=((!(pointerHeld&1)&&!(down&1)&&runtime->is_key_released(VK_LBUTTON))?1:0)
        |((!(pointerHeld&2)&&!(down&2)&&runtime->is_key_released(VK_RBUTTON))?2:0);
    push(mem,4,std::bit_cast<int>(x),std::bit_cast<int>(y),down|tap,wheel,shift);
    if(tap)push(mem,4,std::bit_cast<int>(x),std::bit_cast<int>(y),down,0,shift);
    pointerHeld=down;
}
void poll_gameplay(reshade::api::effect_runtime* runtime,uint8_t* mem,bool active) {
    for(int i=0;i<5;i++) {
        // Use virtual-key constants: ReShade 6.8's implementation orders right
        // and middle differently from the comments in its public header.
        const int vk=i==0?VK_LBUTTON:i==1?VK_RBUTTON:i==2?'E':i==3?'Q':'F';
        const bool down=active && runtime->is_key_down(vk);
        const bool pressed=active && (runtime->is_key_pressed(vk)||(!gameplayHeld[i]&&runtime->is_key_released(vk)));
        if(pressed || (down&&!gameplayHeld[i]))push(mem,3,i,0,0,1);
        if(!down && (gameplayHeld[i]||pressed))push(mem,3,i,0,0,0);
        gameplayHeld[i]=down;
    }
}
void poll(reshade::api::effect_runtime* runtime,uint8_t* mem,bool active) {
    if(!active){wasActive=false;held.fill(false);repeatAt.fill(0);return;}
    const auto now=GetTickCount64();
    BYTE state[256]{};
    for(int i=0;i<256;i++)if(runtime->is_key_down(i))state[i]=0x80;
    state[VK_CAPITAL]|=GetKeyState(VK_CAPITAL)&1;
    int mods=(state[VK_SHIFT]?1:0)|(state[VK_CONTROL]?0x40:0)|(state[VK_MENU]?0x100:0);
    for(int vk=0;vk<256;vk++){
        const int sc=scan(vk);if(!sc)continue;
        const bool down=state[vk]!=0;
        const bool tap=!down && (runtime->is_key_pressed(vk)||(!held[vk]&&runtime->is_key_released(vk)));
        // Do not turn the E that opened the inventory into an immediate close.
        if(!wasActive){held[vk]=down;repeatAt[vk]=now+450;continue;}
        const bool edge=down!=held[vk] || tap;
        const bool repeat=down&&!edge&&now>=repeatAt[vk];
        if(edge||repeat){
            int code=vk>='A'&&vk<='Z'?vk+32:vk;
            push(mem,1,sc,code,mods,(down||tap)?1:0);
            if(tap)push(mem,1,sc,code,mods,0);
            if((down||tap) && !(mods&(0x40|0x100))) {
                WCHAR chars[8]{};
                int count=ToUnicodeEx(vk,MapVirtualKeyW(vk,MAPVK_VK_TO_VSC),state,chars,8,4,GetKeyboardLayout(0));
                for(int j=0;j<count;j++){
                    unsigned cp=chars[j];
                    if(cp>=0xd800&&cp<=0xdbff&&j+1<count){unsigned lo=chars[++j];cp=0x10000+((cp-0xd800)<<10)+(lo-0xdc00);}
                    if(cp>=32&&cp!=127)push(mem,2,cp,0,0,1);
                }
            }
            repeatAt[vk]=now+(edge?450:45);held[vk]=down;
        }
    }
    wasActive=true;
}
}
