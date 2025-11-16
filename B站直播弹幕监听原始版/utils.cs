using System;

namespace BliveDM
{
    public static class Utils
    {
        public const string USER_AGENT =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36";

        /// <summary>
        /// Python: make_constant_retry_policy(interval: float)
        /// </summary>
        public static RetryPolicy make_constant_retry_policy(int intervalMs)
        {
            return (_retry_count, _total_retry_count) => intervalMs;
        }

        /// <summary>
        /// Python: make_linear_retry_policy(start_interval: float, interval_step: float, max_interval: float)
        /// </summary>
        public static RetryPolicy make_linear_retry_policy(int startIntervalMs, int stepMs, int maxIntervalMs)
        {
            return (retry_count, _total_retry_count) =>
            {
                var v = startIntervalMs + (retry_count - 1) * stepMs;
                return v > maxIntervalMs ? maxIntervalMs : v;
            };
        }
    }

    /// <summary>
    /// 对应 Python 的函数签名：get_interval(retry_count, total_retry_count) -> float
    /// </summary>
    public delegate int RetryPolicy(int retry_count, int total_retry_count);
}