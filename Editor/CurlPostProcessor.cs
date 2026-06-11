#if UNITY_IOS
using UnityEditor;
using UnityEditor.Callbacks;
using UnityEditor.iOS.Xcode;
using System.IO;

namespace CurlUnity.Editor
{
    public static class CurlPostProcessor
    {
        [PostProcessBuild(999)]
        public static void OnPostProcessBuild(BuildTarget target, string path)
        {
            if (target != BuildTarget.iOS)
                return;

            var projPath = PBXProject.GetPBXProjectPath(path);
            var proj = new PBXProject();
            proj.ReadFromFile(projPath);

            var targetGuid = proj.GetUnityFrameworkTargetGuid();

            // libcurl 依赖的系统库
            proj.AddFrameworkToProject(targetGuid, "Security.framework", false);
            proj.AddFrameworkToProject(targetGuid, "CoreFoundation.framework", false);
            proj.AddFrameworkToProject(targetGuid, "SystemConfiguration.framework", false);

            // zlib
            proj.AddBuildProperty(targetGuid, "OTHER_LDFLAGS", "-lz");

            proj.WriteToFile(projPath);
        }
    }
}
#endif
