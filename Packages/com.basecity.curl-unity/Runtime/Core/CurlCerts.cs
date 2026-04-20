using System;
using System.IO;
using System.Text;
#if UNITY_ANDROID && !UNITY_EDITOR
using UnityEngine;
#endif
using CurlUnity.Native;

namespace CurlUnity.Core
{
    /// <summary>
    /// 管理 libcurl 所需的 CA 证书。
    /// - macOS / iOS: 无操作（编译时已启用 Apple SecTrust）
    /// - Android: 通过 JNI 提取系统证书存储，写入 PEM 文件供 CURLOPT_CAINFO 使用
    /// </summary>
    internal static class CurlCerts
    {
        private static string _caCertPath;
        private static bool _initialized;

        /// <summary>当前 CA 证书文件路径。Apple 平台返回 null。</summary>
        public static string CACertPath => _caCertPath;

        /// <summary>
        /// 初始化证书。应在 curl_global_init 之后、首次请求之前调用一次。
        /// </summary>
        public static void Initialize()
        {
            if (_initialized) return;
            _initialized = true;

#if UNITY_ANDROID && !UNITY_EDITOR
            InitAndroid();
#endif
        }

        /// <summary>
        /// 对 curl handle 应用 CA 证书配置。
        /// - macOS / iOS: 无操作（编译时启用 Apple SecTrust）
        /// - Android: 设置 CURLOPT_CAINFO 指向提取的 PEM 文件
        /// - Windows: 设置 CURLSSLOPT_NATIVE_CA，通过 CryptoAPI 读取系统证书库
        /// </summary>
        public static void ApplyTo(IntPtr handle)
        {
            ApplyTo(handle, CurlNativeApi.Instance);
        }

        internal static void ApplyTo(IntPtr handle, ICurlApi api)
        {
            if (handle == IntPtr.Zero) return;

            // Android: use extracted PEM file.
            // 失败仅 log warn，不阻止请求继续——后续 TLS 层的失败会比"静默改变信任
            // 行为"更容易被调用方察觉；强行中止请求反而会掩盖 rc 背后的真实原因。
            if (!string.IsNullOrEmpty(_caCertPath))
            {
                var rc = api.SetOptString(handle, CurlNative.CURLOPT_CAINFO, _caCertPath);
                if (rc != CurlNative.CURLE_OK)
                    CurlLog.Warn($"CurlCerts.ApplyTo: CURLOPT_CAINFO returned {rc}; CA store may not be applied as expected.");
            }

#if UNITY_STANDALONE_WIN || UNITY_WSA
            // Windows: use native certificate store via CryptoAPI (curl 7.71.0+)
            var rcSsl = api.SetOptLong(handle, CurlNative.CURLOPT_SSL_OPTIONS,
                CurlNative.CURLSSLOPT_NATIVE_CA);
            if (rcSsl != CurlNative.CURLE_OK)
                CurlLog.Warn($"CurlCerts.ApplyTo: CURLOPT_SSL_OPTIONS (NATIVE_CA) returned {rcSsl}; system cert store may not be in use.");
#endif
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        private static void InitAndroid()
        {
            var pemPath = Path.Combine(Application.persistentDataPath, "curl_cacerts.pem");
            var versionPath = Path.Combine(Application.persistentDataPath, "curl_cacerts.version");

            if (IsCacheValid(versionPath))
            {
                _caCertPath = pemPath;
                Debug.Log($"[CurlCerts] 使用缓存证书: {pemPath}");
                return;
            }

            try
            {
                var pem = ExtractAndroidSystemCerts();
                File.WriteAllText(pemPath, pem, Encoding.ASCII);
                File.WriteAllText(versionPath, GetVersionFingerprint());
                _caCertPath = pemPath;
                Debug.Log($"[CurlCerts] 已提取系统证书 -> {pemPath}");
            }
            catch (Exception e)
            {
                Debug.LogError($"[CurlCerts] 提取系统证书失败: {e}");
            }
        }

        private static bool IsCacheValid(string versionPath)
        {
            if (!File.Exists(versionPath)) return false;

            var pemPath = Path.Combine(Application.persistentDataPath, "curl_cacerts.pem");
            if (!File.Exists(pemPath)) return false;

            try
            {
                var cached = File.ReadAllText(versionPath).Trim();
                return cached == GetVersionFingerprint();
            }
            catch
            {
                return false;
            }
        }

        /// <summary>App 版本 + 系统版本，任一变化即重新提取。</summary>
        private static string GetVersionFingerprint()
        {
            return $"{Application.version}|{SystemInfo.operatingSystem}";
        }

        private static string ExtractAndroidSystemCerts()
        {
            var sb = new StringBuilder(256 * 1024);

            using var tmfClass = new AndroidJavaClass("javax.net.ssl.TrustManagerFactory");
            using var algorithm = tmfClass.CallStatic<AndroidJavaObject>("getDefaultAlgorithm");
            using var tmf = tmfClass.CallStatic<AndroidJavaObject>("getInstance", algorithm);

            // tmf.init((KeyStore) null) — 使用系统默认证书存储
            var nullKeyStore = (AndroidJavaObject)null;
            tmf.Call("init", nullKeyStore);

            var trustManagers = tmf.Call<AndroidJavaObject[]>("getTrustManagers");
            if (trustManagers == null || trustManagers.Length == 0)
                throw new Exception("No TrustManagers found");

            using var tm = trustManagers[0]; // X509TrustManager
            var certs = tm.Call<AndroidJavaObject[]>("getAcceptedIssuers");

            if (certs == null)
                throw new Exception("getAcceptedIssuers returned null");

            int count = 0;
            foreach (var cert in certs)
            {
                if (cert == null) continue;
                try
                {
                    var der = cert.Call<byte[]>("getEncoded");
                    if (der == null || der.Length == 0) continue;

                    sb.AppendLine("-----BEGIN CERTIFICATE-----");
                    sb.AppendLine(Convert.ToBase64String(der, Base64FormattingOptions.InsertLineBreaks));
                    sb.AppendLine("-----END CERTIFICATE-----");
                    count++;
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[CurlCerts] 跳过证书: {e.Message}");
                }
                finally
                {
                    cert.Dispose();
                }
            }

            Debug.Log($"[CurlCerts] 提取了 {count} 个系统 CA 证书");
            return sb.ToString();
        }
#endif
    }
}
