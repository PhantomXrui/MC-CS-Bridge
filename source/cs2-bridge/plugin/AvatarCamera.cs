using System.Drawing;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using V3=System.Numerics.Vector3;

namespace MinecraftBridge;

public sealed partial class MinecraftBridgePlugin
{
    CPointCamera? avatarCamera;
    CCSPlayerPawn? cameraPawn;
    uint previousView=uint.MaxValue;
    int observedCameraMode=-1,angleSamples;
    const uint HiddenEffects=32u|16u|64u; // EF_NODRAW / NOSHADOW / NORECEIVESHADOW
    readonly Dictionary<uint,(CBaseModelEntity entity,uint effects,Color color,RenderMode_t mode)> hiddenAvatar=new();

    void HideAvatarEntity(CBaseModelEntity entity,bool playerBody=false)
    {
        if(!entity.IsValid)return;
        if(!hiddenAvatar.ContainsKey(entity.EntityHandle.Raw))
            hiddenAvatar[entity.EntityHandle.Raw]=(entity,entity.Effects&HiddenEffects,entity.Render,entity.RenderMode);
        // EF_NODRAW also removes the pawn from CS bot perception. Keep its body
        // transparent for the local composite without marking the actor absent.
        uint effects=playerBody&&!interactionSandbox?16u|64u:HiddenEffects;
        if((entity.Effects&HiddenEffects)!=effects){entity.Effects=(entity.Effects&~HiddenEffects)|effects;Utilities.SetStateChanged(entity,"CBaseEntity","m_fEffects");}
        if(entity.Render.A!=0){entity.Render=Color.FromArgb(0,255,255,255);Utilities.SetStateChanged(entity,"CBaseModelEntity","m_clrRender");}
        if(entity.RenderMode!=RenderMode_t.kRenderTransColor){entity.RenderMode=RenderMode_t.kRenderTransColor;Utilities.SetStateChanged(entity,"CBaseModelEntity","m_nRenderMode");}
    }

    void HideLocalAvatar(CCSPlayerPawn pawn)
    {
        HideAvatarEntity(pawn,true);
        if(pawn.WeaponServices!=null)foreach(var handle in pawn.WeaponServices.MyWeapons)
            if(handle.Value is {IsValid:true} weapon)HideAvatarEntity(weapon);
        // Include attached world guns, gloves and cosmetics, but never hide other players or dropped items.
        var stack=new Stack<CGameSceneNode>();
        var root=pawn.CBodyComponent?.SceneNode;
        if(root!=null&&root.Handle!=IntPtr.Zero)stack.Push(root);
        int remaining=64;
        while(stack.Count>0&&remaining-->0){
            var node=stack.Pop();var child=node.Child;
            for(int n=0;child!=null&&child.Handle!=IntPtr.Zero&&n<32;n++,child=child.NextSibling){
                if(child.Owner is {IsValid:true} owner)HideAvatarEntity(owner.As<CBaseModelEntity>());
                stack.Push(child);
            }
        }
    }

    void ReleaseAvatarCamera()
    {
        if(cameraPawn is {IsValid:true} && cameraPawn.CameraServices!=null && avatarCamera is {IsValid:true}
            && cameraPawn.CameraServices.ViewEntity.Raw==avatarCamera.EntityHandle.Raw){
            cameraPawn.CameraServices.ViewEntity.Raw=previousView;
            Utilities.SetStateChanged(cameraPawn,"CBasePlayerPawn","m_pCameraServices");
        }
        if(avatarCamera is {IsValid:true})avatarCamera.Remove();
        avatarCamera=null;cameraPawn=null;
        foreach(var saved in hiddenAvatar.Values)if(saved.entity.IsValid){
            saved.entity.Effects=(saved.entity.Effects&~HiddenEffects)|saved.effects;
            saved.entity.Render=saved.color;saved.entity.RenderMode=saved.mode;
            Utilities.SetStateChanged(saved.entity,"CBaseEntity","m_fEffects");
            Utilities.SetStateChanged(saved.entity,"CBaseModelEntity","m_clrRender");
            Utilities.SetStateChanged(saved.entity,"CBaseModelEntity","m_nRenderMode");
        }
        hiddenAvatar.Clear();view?.Write(236,0);view?.Write(240,0f);
    }

    int UpdateAvatarCamera(CCSPlayerPawn pawn,bool enabled,ref float[] camera,ref float yaw,ref float pitch)
    {
        if(!enabled){ReleaseAvatarCamera();return 0;}
        if(cameraPawn is {IsValid:true} && cameraPawn.EntityHandle.Raw!=pawn.EntityHandle.Raw)ReleaseAvatarCamera();
        HideLocalAvatar(pawn);
        int mode=Math.Clamp(view!.ReadInt32(232),0,2);
        if(observedCameraMode!=mode){observedCameraMode=mode;angleSamples=10;}
        if(angleSamples-->0)Log($"avatar mode={mode} input={pawn.V_angle.X},{pawn.V_angle.Y} eye={pawn.EyeAngles.X},{pawn.EyeAngles.Y}");
        if(mode==0 && !MinecraftMovementActive){
            if(avatarCamera!=null){ReleaseAvatarCamera();HideLocalAvatar(pawn);}
            view.Write(236,0);view.Write(240,0f);return 0;
        }
        if(avatarCamera is not {IsValid:true}){
            avatarCamera=Utilities.CreateEntityByName<CPointCamera>("point_camera");
            if(avatarCamera==null){view.Write(232,0);Log("Could not create local avatar camera");return 0;}
            avatarCamera.FOV=90;avatarCamera.ZNear=6.5f;avatarCamera.ZFar=16384f;avatarCamera.UseScreenAspectRatio=true;
            avatarCamera.DispatchSpawn();cameraPawn=pawn;previousView=pawn.CameraServices!.ViewEntity.Raw;
        }
        var eye=WorldGeometry.ToCS(new V3(camera[0],camera[1],camera[2]));
        float ya=pawn.V_angle.Y*MathF.PI/180,pi=pawn.V_angle.X*MathF.PI/180;
        var forward=new V3(MathF.Cos(pi)*MathF.Cos(ya),MathF.Cos(pi)*MathF.Sin(ya),-MathF.Sin(pi));
        var desired=mode==0?eye:eye+forward*(mode==1?-160:160);
        var at=mode==0?eye:SafeAvatarCamera(eye,desired);
        var mc=WorldGeometry.ToMC(at);camera=[mc.X,mc.Y,mc.Z];
        float csPitch=pawn.V_angle.X,csYaw=pawn.V_angle.Y;
        if(mode==2){yaw+=180;pitch=-pitch;csYaw+=180;csPitch=-csPitch;}
        avatarCamera.Teleport(new Vector(at.X,at.Y,at.Z),new QAngle(csPitch,csYaw,0),new Vector(0,0,0));
        if(pawn.CameraServices!.ViewEntity.Raw!=avatarCamera.EntityHandle.Raw){
            pawn.CameraServices.ViewEntity.Raw=avatarCamera.EntityHandle.Raw;
            Utilities.SetStateChanged(pawn,"CBasePlayerPawn","m_pCameraServices");
        }
        view.Write(236,mode);view.Write(240,V3.Distance(at,eye)/Units);
        return mode;
    }

    V3 SafeAvatarCamera(V3 eye,V3 desired)
    {
        var delta=desired-eye;float distance=delta.Length(),safe=distance;
        if(distance<.001f)return eye;
        // Sweep a small camera volume with its centre and eight corners against the local map mesh.
        for(int i=0;i<9;i++){
            var off=i==0?V3.Zero:new V3(((i-1)&1)==0?-8:8,((i-1)&2)==0?-8:8,((i-1)&4)==0?-8:8);
            if(geometry?.Ray(eye+off,desired+off,out var hit)==true)safe=MathF.Min(safe,MathF.Max(0,V3.Distance(eye+off,hit)-2));
        }
        // User-built blocks are separate from the static host mesh.
        foreach(var b in blocks.Keys){
            var min=WorldGeometry.ToCS(new V3(b.Item1,b.Item2,b.Item3+1))-new V3(8);
            var max=min+new V3(Units+16);
            if(CameraBoxFraction(eye,delta,min,max,out float fraction))safe=MathF.Min(safe,MathF.Max(0,distance*fraction-2));
        }
        return eye+delta*(safe/distance);
    }

    internal static bool CameraBoxFraction(V3 start,V3 delta,V3 min,V3 max,out float fraction)
    {
        float enter=0,exit=1;
        for(int a=0;a<3;a++){
            if(MathF.Abs(delta[a])<1e-6f){if(start[a]<min[a]||start[a]>max[a]){fraction=1;return false;}continue;}
            float t0=(min[a]-start[a])/delta[a],t1=(max[a]-start[a])/delta[a];
            if(t0>t1)(t0,t1)=(t1,t0);enter=MathF.Max(enter,t0);exit=MathF.Min(exit,t1);
            if(enter>exit){fraction=1;return false;}
        }
        fraction=enter;return true;
    }
}
