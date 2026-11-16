using MelonLoader;
using UnityEngine;

namespace RumbleReShaded
{
    // Each eye's position and lens, as shader globals, for packs that put things in the world: a ray through a pixel
    // from where the eye really is. Unity's own per-eye matrices should give the same, but a pack drawing sparkles in
    // the air showed them on the surfaces behind instead in VR, as if both eyes shared one
    // position - these are worked out here from the camera, so a pack doesn't depend on what the blit happens to bind.
    //
    //   _RRS_eyes                                     1 once these are set
    //   _RRS_eyeToWorld0 / 1                          left / right eye to world (view space looks down -Z)
    //   _RRS_eyeInvProjection0 / 1                    the inverse of each eye's projection, OpenGL style (not flipped
    //                                                 for the GPU): NDC (uv * 2 - 1, -1, 1) to a point on the near plane
    //
    // With no headset both are the camera's own. Set before the pass is enqueued, like the event globals.
    internal static class EyeGlobals
    {
        private static bool logged;

        public static void Push(Camera cam)
        {
            Matrix4x4 leftView, rightView, leftProjection, rightProjection;
            if (cam.stereoEnabled)
            {
                leftView = cam.GetStereoViewMatrix(Camera.StereoscopicEye.Left);
                rightView = cam.GetStereoViewMatrix(Camera.StereoscopicEye.Right);
                leftProjection = cam.GetStereoProjectionMatrix(Camera.StereoscopicEye.Left);
                rightProjection = cam.GetStereoProjectionMatrix(Camera.StereoscopicEye.Right);
            }
            else
            {
                leftView = rightView = cam.worldToCameraMatrix;
                leftProjection = rightProjection = cam.projectionMatrix;
            }

            Matrix4x4 leftToWorld = leftView.inverse, rightToWorld = rightView.inverse;
            Shader.SetGlobalMatrix("_RRS_eyeToWorld0", leftToWorld);
            Shader.SetGlobalMatrix("_RRS_eyeToWorld1", rightToWorld);
            Shader.SetGlobalMatrix("_RRS_eyeInvProjection0", leftProjection.inverse);
            Shader.SetGlobalMatrix("_RRS_eyeInvProjection1", rightProjection.inverse);
            Shader.SetGlobalFloat("_RRS_eyes", 1f);

            if (!logged)
            {
                logged = true;
                // For reports of things sitting at the wrong depth: a headset's eyes are about 0.06 m apart.
                float apart = Vector3.Distance(leftToWorld.GetColumn(3), rightToWorld.GetColumn(3));
                Logger.Debug($"[diag] Eye globals on '{cam.name}' (stereo={cam.stereoEnabled}): eyes {apart:0.000} m apart.");
            }
        }
    }
}
