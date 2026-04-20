using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using CurlUnity.Native;

namespace CurlUnity.UnitTests.TestSupport
{
    internal sealed class FakeCurlApi : ICurlApi
    {
        private long _nextEasyHandle = 1;
        private long _nextMultiHandle = 10_000;
        private long _nextSListHandle = 20_000;

        public int CurlGlobalInitResult { get; set; } = CurlNative.CURLE_OK;
        public int MultiAddHandleResult { get; set; } = CurlNative.CURLE_OK;
        public int MultiRemoveHandleResult { get; set; } = CurlNative.CURLE_OK;

        public int CurlGlobalInitCalls { get; private set; }
        public int CurlGlobalCleanupCalls { get; private set; }
        public int MultiCleanupCalls { get; private set; }

        public bool PollInProgress { get; private set; }
        public bool CallbackInProgress { get; private set; }
        public bool MultiCleanupCalledWhilePollInProgress { get; private set; }
        public bool MultiCleanupCalledWhileCallbackInProgress { get; private set; }

        public Action<IntPtr> OnMultiPerform { get; set; }

        /// <summary>
        /// 覆盖 <see cref="MultiPoll"/> 的默认行为。参数为 (multi handle, timeoutMs)。
        /// 默认行为模拟真实 libcurl：等待被 <see cref="MultiWakeup"/> 唤醒或 timeoutMs 到期。
        /// </summary>
        public Action<IntPtr, int> OnMultiPoll { get; set; }

        private readonly Dictionary<IntPtr, FakeEasyHandleState> _easyHandles = new();
        private readonly Dictionary<IntPtr, FakeMultiHandleState> _multiHandles = new();
        private readonly Dictionary<IntPtr, List<string>> _sLists = new();

        // 唤醒/超时信号，用于 MultiPoll 的默认实现。保持和 libcurl 一致的语义：
        // 每次 poll 返回后自动复位，这样下一次 poll 又能阻塞直到 timeoutMs 或新的 wakeup。
        private readonly ManualResetEventSlim _pollWakeup = new(false);

        public int CurlGlobalInit(long flags)
        {
            CurlGlobalInitCalls++;
            return CurlGlobalInitResult;
        }

        public void CurlGlobalCleanup()
        {
            CurlGlobalCleanupCalls++;
        }

        public IntPtr EasyInit()
        {
            var handle = new IntPtr(_nextEasyHandle++);
            _easyHandles[handle] = new FakeEasyHandleState();
            return handle;
        }

        public void EasyCleanup(IntPtr handle)
        {
            if (_easyHandles.TryGetValue(handle, out var state))
                state.IsCleanedUp = true;
        }

        public IntPtr SListAppend(IntPtr list, string value)
        {
            if (list == IntPtr.Zero)
            {
                list = new IntPtr(_nextSListHandle++);
                _sLists[list] = new List<string>();
            }

            _sLists[list].Add(value);
            return list;
        }

        public void SListFreeAll(IntPtr list)
        {
            _sLists.Remove(list);
        }

        public int SetOptString(IntPtr handle, int option, string value)
        {
            _easyHandles[handle].StringOptions[option] = value;
            return CurlNative.CURLE_OK;
        }

        public int SetOptLong(IntPtr handle, int option, long value)
        {
            _easyHandles[handle].LongOptions[option] = value;
            return CurlNative.CURLE_OK;
        }

        public int SetOptPtr(IntPtr handle, int option, IntPtr value)
        {
            _easyHandles[handle].PointerOptions[option] = value;
            return CurlNative.CURLE_OK;
        }

        public int SetOptOffT(IntPtr handle, int option, long value)
        {
            _easyHandles[handle].OffTOptions[option] = value;
            return CurlNative.CURLE_OK;
        }

        public int SetOptWriteFunction(IntPtr handle, CurlNative.WriteCallback callback)
        {
            _easyHandles[handle].WriteCallback = callback;
            return CurlNative.CURLE_OK;
        }

        public int SetOptWriteData(IntPtr handle, IntPtr userdata)
        {
            _easyHandles[handle].WriteData = userdata;
            return CurlNative.CURLE_OK;
        }

        public int SetOptHeaderFunction(IntPtr handle, CurlNative.WriteCallback callback)
        {
            _easyHandles[handle].HeaderCallback = callback;
            return CurlNative.CURLE_OK;
        }

        public int SetOptHeaderData(IntPtr handle, IntPtr userdata)
        {
            _easyHandles[handle].HeaderData = userdata;
            return CurlNative.CURLE_OK;
        }

        public int GetInfoLong(IntPtr handle, int info, out long value)
        {
            if (info == CurlNative.CURLINFO_RESPONSE_CODE)
            {
                value = _easyHandles[handle].ResponseCode;
                return CurlNative.CURLE_OK;
            }

            if (_easyHandles[handle].InfoLong.TryGetValue(info, out value))
                return CurlNative.CURLE_OK;

            value = 0;
            return CurlNative.CURLE_OK;
        }

        public int GetInfoString(IntPtr handle, int info, out IntPtr value)
        {
            if (info == CurlNative.CURLINFO_PRIVATE &&
                _easyHandles[handle].PointerOptions.TryGetValue(CurlNative.CURLOPT_PRIVATE, out value))
                return CurlNative.CURLE_OK;

            if (_easyHandles[handle].InfoString.TryGetValue(info, out value))
                return CurlNative.CURLE_OK;

            value = IntPtr.Zero;
            return CurlNative.CURLE_OK;
        }

        public int GetInfoDouble(IntPtr handle, int info, out double value)
        {
            if (_easyHandles[handle].InfoDouble.TryGetValue(info, out value))
                return CurlNative.CURLE_OK;

            value = 0;
            return CurlNative.CURLE_OK;
        }

        public int GetInfoOffT(IntPtr handle, int info, out long value)
        {
            if (_easyHandles[handle].InfoOffT.TryGetValue(info, out value))
                return CurlNative.CURLE_OK;

            value = 0;
            return CurlNative.CURLE_OK;
        }

        public IntPtr MultiInit()
        {
            var handle = new IntPtr(_nextMultiHandle++);
            _multiHandles[handle] = new FakeMultiHandleState();
            return handle;
        }

        public int MultiCleanup(IntPtr multi)
        {
            MultiCleanupCalls++;
            if (PollInProgress)
                MultiCleanupCalledWhilePollInProgress = true;
            if (CallbackInProgress)
                MultiCleanupCalledWhileCallbackInProgress = true;

            if (_multiHandles.TryGetValue(multi, out var state))
                state.IsCleanedUp = true;

            return CurlNative.CURLE_OK;
        }

        public int MultiAddHandle(IntPtr multi, IntPtr easy)
        {
            var result = MultiAddHandleResult;
            if (result == CurlNative.CURLE_OK && _multiHandles.TryGetValue(multi, out var state))
                state.ActiveHandles.Add(easy);
            return result;
        }

        public int MultiRemoveHandle(IntPtr multi, IntPtr easy)
        {
            var result = MultiRemoveHandleResult;
            // 与 libcurl 语义一致：仅在调用成功时 handle 才真正离开 multi。
            // 非 OK 返回意味着 multi 还持有这个 handle，Fake 必须保留它以便
            // 测试能观察到"remove 失败的请求仍然活跃"这一真实产品行为。
            if (result == CurlNative.CURLE_OK && _multiHandles.TryGetValue(multi, out var state))
                state.ActiveHandles.Remove(easy);
            return result;
        }

        public int MultiPerform(IntPtr multi, out int runningHandles)
        {
            if (!_multiHandles.TryGetValue(multi, out var state))
            {
                runningHandles = 0;
                return CurlNative.CURLE_OK;
            }

            OnMultiPerform?.Invoke(multi);
            runningHandles = state.ActiveHandles.Count;
            return CurlNative.CURLE_OK;
        }

        public int MultiPoll(IntPtr multi, IntPtr extraFds, uint extraNfds, int timeoutMs, out int numFds)
        {
            PollInProgress = true;
            try
            {
                if (OnMultiPoll != null)
                {
                    OnMultiPoll(multi, timeoutMs);
                }
                else
                {
                    // 默认行为：按 timeoutMs 阻塞，遇到 Wakeup 立刻返回。
                    // 这和真实 libcurl 的合约一致，避免假 API 让测试做出错误的"poll 会永远
                    // 阻塞"假设。
                    _pollWakeup.Wait(timeoutMs);
                    _pollWakeup.Reset();
                }
                numFds = 0;
                return CurlNative.CURLE_OK;
            }
            finally
            {
                PollInProgress = false;
            }
        }

        public int MultiWakeup(IntPtr multi)
        {
            _pollWakeup.Set();
            return CurlNative.CURLE_OK;
        }

        public int MultiInfoRead(IntPtr multi, out IntPtr easyHandle, out int result)
        {
            if (_multiHandles.TryGetValue(multi, out var state) && state.CompletedHandles.Count > 0)
            {
                var item = state.CompletedHandles.Dequeue();
                easyHandle = item.easyHandle;
                result = item.result;
                return 1;
            }

            easyHandle = IntPtr.Zero;
            result = CurlNative.CURLE_OK;
            return 0;
        }

        public string GetErrorString(int code) => $"fake-error-{code}";

        public string GetMultiErrorString(int code) => $"fake-multi-error-{code}";

        public IntPtr GetFirstActiveHandle(IntPtr multi)
        {
            return _multiHandles[multi].ActiveHandles.FirstOrDefault();
        }

        public void InvokeWriteCallback(IntPtr easyHandle, byte[] payload)
        {
            var state = _easyHandles[easyHandle];
            if (state.WriteCallback == null)
                throw new InvalidOperationException("Write callback has not been registered.");

            var pin = GCHandle.Alloc(payload, GCHandleType.Pinned);
            try
            {
                CallbackInProgress = true;
                state.WriteCallback(pin.AddrOfPinnedObject(), (UIntPtr)1, (UIntPtr)payload.Length, state.WriteData);
            }
            finally
            {
                CallbackInProgress = false;
                pin.Free();
            }
        }

        public sealed class FakeEasyHandleState
        {
            public readonly Dictionary<int, string> StringOptions = new();
            public readonly Dictionary<int, long> LongOptions = new();
            public readonly Dictionary<int, IntPtr> PointerOptions = new();
            public readonly Dictionary<int, long> OffTOptions = new();
            public readonly Dictionary<int, long> InfoLong = new();
            public readonly Dictionary<int, IntPtr> InfoString = new();
            public readonly Dictionary<int, double> InfoDouble = new();
            public readonly Dictionary<int, long> InfoOffT = new();
            public CurlNative.WriteCallback WriteCallback;
            public IntPtr WriteData;
            public CurlNative.WriteCallback HeaderCallback;
            public IntPtr HeaderData;
            public long ResponseCode;
            public bool IsCleanedUp;
        }

        public sealed class FakeMultiHandleState
        {
            public readonly HashSet<IntPtr> ActiveHandles = new();
            public readonly Queue<(IntPtr easyHandle, int result)> CompletedHandles = new();
            public bool IsCleanedUp;
        }
    }
}
