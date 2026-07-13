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
        private static readonly object s_initLock = new();
        // volatile: _caCertPath 在锁内写、ApplyTo 无锁读，保证安全发布
        private static volatile string _caCertPath;
        private static volatile bool _initialized;

        /// <summary>当前 CA 证书文件路径。Apple 平台返回 null。</summary>
        public static string CACertPath => _caCertPath;

        /// <summary>
        /// 初始化证书。应在 curl_global_init 之后、首次请求之前调用一次；
        /// 并发调用安全（多个 client 同时首构时只初始化一次）。
        /// <para>
        /// Android 上提取失败时<b>不置位</b>，下一个 client 构造时会重试——
        /// 否则一次瞬态失败（如在未 attach JNI 的后台线程构造 client）会让
        /// 整个进程到重启为止都没有 CA、所有 HTTPS 全挂。Android 平台建议在
        /// 主线程构造首个 client（JNI 访问需要已 attach 的线程）。
        /// </para>
        /// </summary>
        public static void Initialize()
        {
            if (_initialized) return;
            lock (s_initLock)
            {
                if (_initialized) return;

#if UNITY_ANDROID && !UNITY_EDITOR
                if (!TryInitAndroid())
                    return; // 失败不置位，保留下次重试机会
#endif
                _initialized = true;
            }
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
        /// <summary>成功（或命中缓存）返回 true；提取失败返回 false，由调用方决定重试。</summary>
        private static bool TryInitAndroid()
        {
            var pemPath = Path.Combine(Application.persistentDataPath, "curl_cacerts.pem");
            var versionPath = Path.Combine(Application.persistentDataPath, "curl_cacerts.version");

            if (IsCacheValid(versionPath))
            {
                _caCertPath = pemPath;
                Debug.Log($"[CurlCerts] 使用缓存证书: {pemPath}");
                return true;
            }

            try
            {
                var pem = ExtractAndroidSystemCerts();
                File.WriteAllText(pemPath, pem, Encoding.ASCII);
                File.WriteAllText(versionPath, GetVersionFingerprint());
                _caCertPath = pemPath;
                Debug.Log($"[CurlCerts] 已提取系统证书 -> {pemPath}");
                return true;
            }
            catch (Exception e)
            {
                Debug.LogError($"[CurlCerts] 提取系统证书失败（下一个 client 构造时将重试）: {e}");
                return false;
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
