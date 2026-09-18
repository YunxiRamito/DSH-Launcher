using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace DeepSeekHarnessLauncher
{
    /// <summary>
    /// 判断这台机器在不在中国大陆。
    ///
    /// 用途:下载源的选择。在国内给 GitHub 套 CDN 加速前缀,在国外直接用原始地址。
    /// 判断顺序(任何一步失败就往下走):
    ///   1. Windows 地理位置 API(GetUserGeoID → 国家代码)
    ///   2. 系统时区(中国标准时间)
    ///   3. 系统区域设置(zh-CN)
    /// 结果缓存,只算一次。
    /// </summary>
    internal static class RegionInfo
    {
        private const int GeoIso2 = 4;        // GEO_ISO2:两位国家代码
        private const int GeoNameChina = 45;  // 中国大陆的 GEOID

        private static bool _resolved;
        private static bool _isChina;
        private static string _reason;

        public static bool IsChinaMainland
        {
            get
            {
                if (!_resolved)
                {
                    _isChina = Detect(out _reason);
                    _resolved = true;
                }

                return _isChina;
            }
        }

        /// <summary>给日志用:为什么这么判断。</summary>
        public static string Reason
        {
            get
            {
                if (!_resolved)
                {
                    _isChina = Detect(out _reason);
                    _resolved = true;
                }

                return _reason;
            }
        }

        private static bool Detect(out string reason)
        {
            // 1) 地理位置
            try
            {
                int geoId = GetUserGeoID(GeoIso2);
                if (geoId == GeoNameChina)
                {
                    reason = "地理位置 GeoID=" + geoId + "(中国大陆)";
                    return true;
                }

                if (geoId > 0)
                {
                    reason = "地理位置 GeoID=" + geoId + "(非中国大陆)";
                    return false;
                }
            }
            catch
            {
            }

            // 2) 时区
            try
            {
                TimeZoneInfo zone = TimeZoneInfo.Local;
                string id = zone != null ? zone.Id : null;
                if (!string.IsNullOrEmpty(id)
                    && (id.IndexOf("China Standard Time", StringComparison.OrdinalIgnoreCase) >= 0
                        || id.IndexOf("Asia/Shanghai", StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    reason = "时区 " + id;
                    return true;
                }
            }
            catch
            {
            }

            // 3) 区域设置
            try
            {
                string name = CultureInfo.CurrentCulture.Name;
                if (!string.IsNullOrEmpty(name)
                    && name.StartsWith("zh-CN", StringComparison.OrdinalIgnoreCase))
                {
                    reason = "区域 " + name;
                    return true;
                }
            }
            catch
            {
            }

            reason = "判定为国外(地理/时区/区域都没命中中国大陆)";
            return false;
        }

        [DllImport("kernel32.dll")]
        private static extern int GetUserGeoID(int geoClass);
    }
}
