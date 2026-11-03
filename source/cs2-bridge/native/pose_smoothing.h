#pragma once
#include <algorithm>
#include <cmath>
#include <cstdint>
#include <cstring>

// A local server publishes at 64 Hz; presentation may occur between its ticks.
// Predict at most ONE measured tick, with resets for teleports and large turns.
// This is an approximation, not a replacement for sampling Source 2's client view.
struct PoseSmoothing {
    uint64_t sequence=0;
    double stamp=0,interval=0;
    float current[9]{},delta[5]{};
    void reset(){sequence=0;stamp=interval=0;}
    void sample(uint64_t seq,double time,const float *p){
        if(seq==sequence)return;
        double dt=time-stamp;
        float distance=0;for(int i=0;i<3;++i){float d=p[i]-current[i];distance+=d*d;}
        float yaw=std::remainder(p[3]-current[3],360.0f);
        bool valid=sequence && dt>=5 && dt<=50 && distance<4 && std::abs(yaw)<45 && std::abs(p[4]-current[4])<45;
        for(int i=0;i<5;++i)delta[i]=valid?(i==3?yaw:p[i]-current[i]):0;
        interval=valid?dt:0;stamp=time;sequence=seq;std::memcpy(current,p,sizeof current);
    }
    void at(double now,float *p) const {
        std::memcpy(p,current,sizeof current);
        const float t=interval>0?float(std::clamp((now-stamp)/interval,0.0,1.0)):0;
        for(int i=0;i<5;++i)p[i]+=delta[i]*t;
    }
};
