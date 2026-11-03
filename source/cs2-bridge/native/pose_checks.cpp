#include "pose_smoothing.h"
#include <cassert>
#include <cstdio>
int main(){
 PoseSmoothing s;float p[9]{},out[9]{};
 s.sample(2,1000,p);s.at(1008,out);assert(out[0]==0);
 p[0]=.125f;s.sample(4,1015.625,p);s.at(1023.4375,out);assert(std::abs(out[0]-.1875f)<1e-5);
 s.at(2000,out);assert(std::abs(out[0]-.25f)<1e-5); // Never drift while stalled.
 p[0]=20;s.sample(6,1031.25,p);s.at(1038,out);assert(out[0]==20); // Teleport resets.
 s.reset();p[3]=179;s.sample(2,2000,p);p[3]=-179;s.sample(4,2015.625,p);s.at(2023.4375,out);assert(std::abs(out[3]+178)<1e-5);
 p[3]=-90;s.sample(6,2031.25,p);s.at(2038,out);assert(out[3]==-90); // Abrupt turn resets.
 s.sample(6,2039,p);s.at(2040,out);assert(out[3]==-90); // Repeated render doesn't invent a tick.
 puts("Pose checks passed: between-tick movement, stall clamp, teleport, angle wrap, abrupt turn.");
}
