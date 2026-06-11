namespace CurlUnity.Core
{
    /// <summary>
    /// Best-effort logging for native call failures and other non-fatal conditions.
    /// Not user-facing. In Unity goes to <c>UnityEngine.Debug</c>; elsewhere (tests,
    /// tooling) goes to <c>stderr</c>.
    /// </summary>
    internal static class CurlLog
    {
        private const string Prefix = "[curl-unity] ";

#if UNITY_5_3_OR_NEWER
        public static void Warn(string message)
            => UnityEngine.Debug.LogWarning(Prefix + message);

        public static void Error(string message)
            => UnityEngine.Debug.LogError(Prefix + message);
#else
        public static void Warn(string message)
            => System.Console.Error.WriteLine(Prefix + "WARN: " + message);

        public static void Error(string message)
            => System.Console.Error.WriteLine(Prefix + "ERROR: " + message);
#endif
    }
}
