using System.Collections;
using System.Globalization;
using System.Net;
using System.Reflection;
using System.Runtime.Serialization.Formatters.Binary;
using DownKyi.Core.Logging;
using Console = DownKyi.Core.Utils.Debugging.Console;

namespace DownKyi.Core.Utils;

public static class ObjectHelper
{
    /// <summary>
    /// B站网页登录 Cookie 的默认有效期，约 180 天。
    /// </summary>
    private const int DefaultCookieLifetimeSeconds = 15552000;

    private static readonly string[] LoginCookieNames =
    {
        "SESSDATA",
        "bili_jct",
        "DedeUserID",
        "DedeUserID__ckMd5",
        "sid"
    };

    private static readonly Uri[] LoginCookieUris =
    {
        new("https://passport.bilibili.com/"),
        new("https://www.bilibili.com/"),
        new("https://api.bilibili.com/")
    };

    /// <summary>
    /// 解析二维码登录返回的url，用于设置cookie
    /// </summary>
    /// <param name="url"></param>
    /// <returns></returns>
    public static CookieContainer ParseCookie(string? url)
    {
        return MergeLoginCookies(url, null);
    }

    /// <summary>
    /// 合并跨域登录 URL 中的参数和 poll 响应 Set-Cookie。响应 Cookie 优先。
    /// </summary>
    public static CookieContainer MergeLoginCookies(string? url, IEnumerable<Cookie>? responseCookies)
    {
        var merged = new Dictionary<string, Cookie>(StringComparer.Ordinal);
        foreach (var cookie in ParseUrlCookies(url))
        {
            merged[cookie.Name] = cookie;
        }

        if (responseCookies != null)
        {
            foreach (var cookie in responseCookies)
            {
                var normalized = CreateBilibiliCookie(cookie.Name, cookie.Value, cookie.Expires);
                if (normalized != null)
                {
                    merged[normalized.Name] = normalized;
                }
            }
        }

        var cookieContainer = new CookieContainer();
        foreach (var cookie in merged.Values)
        {
            try
            {
                cookieContainer.Add(cookie);
            }
            catch (CookieException e)
            {
                Console.PrintLine("添加Cookie失败: {0} {1}", cookie.Name, e.Message);
            }
        }

        return cookieContainer;
    }

    /// <summary>
    /// 读取扫码登录响应里的 Set-Cookie。已有登录态只作底稿，本次响应里的同名 Cookie 会覆盖它。
    /// </summary>
    public static void CollectResponseCookies(HttpWebRequest request, HttpWebResponse response, ICollection<Cookie> target)
    {
        var merged = new Dictionary<string, Cookie>(StringComparer.Ordinal);
        if (request.CookieContainer != null)
        {
            foreach (var uri in LoginCookieUris)
            {
                foreach (Cookie cookie in request.CookieContainer.GetCookies(uri))
                {
                    if (!LoginCookieNames.Contains(cookie.Name))
                    {
                        continue;
                    }

                    var created = CreateBilibiliCookie(cookie.Name, cookie.Value, cookie.Expires);
                    if (created != null)
                    {
                        merged[created.Name] = created;
                    }
                }
            }
        }

        foreach (Cookie cookie in response.Cookies)
        {
            var created = CreateBilibiliCookie(cookie.Name, cookie.Value, cookie.Expires);
            if (created != null)
            {
                merged[created.Name] = created;
            }
        }

        for (var i = 0; i < response.Headers.Count; i++)
        {
            if (!string.Equals(response.Headers.GetKey(i), "Set-Cookie", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var values = response.Headers.GetValues(i);
            if (values == null)
            {
                continue;
            }

            foreach (var header in values)
            {
                ParseSetCookieHeader(header, merged);
            }
        }

        foreach (var cookie in merged.Values)
        {
            target.Add(cookie);
        }
    }

    public static Cookie? CreateBilibiliCookie(string? name, string? value, DateTime expires)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrEmpty(value))
        {
            return null;
        }

        try
        {
            name = Uri.UnescapeDataString(name.Trim());
        }
        catch (UriFormatException)
        {
            name = name.Trim();
        }

        if (IsCookieAttribute(name))
        {
            return null;
        }

        try
        {
            value = Uri.UnescapeDataString(value.Trim());
        }
        catch (UriFormatException)
        {
            value = value.Trim();
        }

        value = value.Replace(",", "%2c").Replace(";", "%3b");
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        if (expires == DateTime.MinValue)
        {
            expires = DateTime.Now.AddSeconds(DefaultCookieLifetimeSeconds);
        }

        return new Cookie(name, value, "/", ".bilibili.com")
        {
            Expires = expires,
            HttpOnly = name == "SESSDATA",
            Secure = true
        };
    }

    private static IEnumerable<Cookie> ParseUrlCookies(string? url)
    {
        if (string.IsNullOrEmpty(url))
        {
            yield break;
        }

        var queryIndex = url.IndexOf('?');
        if (queryIndex < 0 || queryIndex >= url.Length - 1)
        {
            yield break;
        }

        var query = url[(queryIndex + 1)..];
        var hashIndex = query.IndexOf('#');
        if (hashIndex >= 0)
        {
            query = query[..hashIndex];
        }

        var pairs = query.Split('&', StringSplitOptions.RemoveEmptyEntries);
        var expires = ReadUrlExpires(pairs);
        foreach (var pair in pairs)
        {
            var parts = pair.Split('=', 2);
            if (parts.Length < 2)
            {
                continue;
            }

            var cookie = CreateBilibiliCookie(parts[0], parts[1], expires);
            if (cookie != null)
            {
                yield return cookie;
            }
        }
    }

    private static DateTime ReadUrlExpires(string[] pairs)
    {
        var expires = DateTime.Now.AddSeconds(DefaultCookieLifetimeSeconds);
        foreach (var pair in pairs)
        {
            if (!pair.StartsWith("Expires=", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var raw = pair["Expires=".Length..];
            if (!long.TryParse(raw, out var seconds) || seconds <= 0)
            {
                break;
            }

            if (seconds > 10_000_000_000)
            {
                expires = DateTimeOffset.FromUnixTimeMilliseconds(seconds).LocalDateTime;
            }
            else if (seconds > 315360000)
            {
                expires = DateTimeOffset.FromUnixTimeSeconds(seconds).LocalDateTime;
            }
            else
            {
                expires = DateTime.Now.AddSeconds(seconds);
            }

            break;
        }

        return expires;
    }

    private static void ParseSetCookieHeader(string header, IDictionary<string, Cookie> merged)
    {
        if (string.IsNullOrWhiteSpace(header))
        {
            return;
        }

        var parts = header.Split(';');
        var nameValue = parts[0].Split('=', 2);
        if (nameValue.Length < 2)
        {
            return;
        }

        var expires = DateTime.MinValue;
        foreach (var part in parts.Skip(1))
        {
            var item = part.Trim();
            const string expiresPrefix = "expires=";
            if (!item.StartsWith(expiresPrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var dateText = item[expiresPrefix.Length..];
            if (DateTime.TryParse(dateText, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
            {
                expires = parsed.ToLocalTime();
            }

            break;
        }

        var cookie = CreateBilibiliCookie(nameValue[0], nameValue[1], expires);
        if (cookie != null)
        {
            merged[cookie.Name] = cookie;
        }
    }

    private static bool IsCookieAttribute(string name)
    {
        return name.Equals("Expires", StringComparison.OrdinalIgnoreCase)
               || name.Equals("gourl", StringComparison.OrdinalIgnoreCase)
               || name.Equals("Path", StringComparison.OrdinalIgnoreCase)
               || name.Equals("Domain", StringComparison.OrdinalIgnoreCase)
               || name.Equals("Max-Age", StringComparison.OrdinalIgnoreCase)
               || name.Equals("SameSite", StringComparison.OrdinalIgnoreCase)
               || name.Equals("HttpOnly", StringComparison.OrdinalIgnoreCase)
               || name.Equals("Secure", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 将CookieContainer中的所有的Cookie读出来
    /// </summary>
    /// <param name="cc"></param>
    /// <returns></returns>
    public static List<Cookie> GetAllCookies(CookieContainer cc)
    {
        var lstCookies = new List<Cookie>();

        var table = (Hashtable?)cc.GetType().InvokeMember("m_domainTable",
            BindingFlags.NonPublic | BindingFlags.GetField |
            BindingFlags.Instance, null, cc, new object[] { });

        foreach (var pathList in table?.Values ?? Array.Empty<Hashtable>())
        {
            var lstCookieCol = (SortedList?)pathList.GetType().InvokeMember("m_list",
                BindingFlags.NonPublic | BindingFlags.GetField | BindingFlags.Instance, null, pathList,
                Array.Empty<object>());
            foreach (CookieCollection colCookies in lstCookieCol?.Values ?? Array.Empty<CookieCollection>())
            {
                foreach (Cookie c in colCookies)
                {
                    lstCookies.Add(c);
                }
            }
        }

        return lstCookies;
    }

    /// <summary>
    /// 写入cookies到磁盘
    /// </summary>
    /// <param name="file"></param>
    /// <param name="cookieJar"></param>
    /// <returns></returns>
    public static bool WriteCookiesToDisk(string file, CookieContainer cookieJar)
    {
        return WriteObjectToDisk(file, cookieJar);
    }

    /// <summary>
    /// 从磁盘读取cookie
    /// </summary>
    /// <param name="file"></param>
    /// <returns></returns>
    public static CookieContainer? ReadCookiesFromDisk(string file)
    {
        return (CookieContainer?)ReadObjectFromDisk(file);
    }

    /// <summary>
    /// 写入序列化对象到磁盘
    /// </summary>
    /// <param name="file"></param>
    /// <param name="obj"></param>
    /// <returns></returns>
    public static bool WriteObjectToDisk(string file, object obj)
    {
        try
        {
            using Stream stream = File.Create(file);
            Console.PrintLine("Writing object to disk... ");

            var formatter = new BinaryFormatter();
            formatter.Serialize(stream, obj);

            Console.PrintLine("Done.");
            return true;
        }
        catch (IOException e)
        {
            Console.PrintLine("WriteObjectToDisk()发生IO异常: {0}", e);
            LogManager.Error(e);
            return false;
        }
        catch (Exception e)
        {
            Console.PrintLine("WriteObjectToDisk()发生异常: {0}", e);
            LogManager.Error(e);
            return false;
        }
    }

    /// <summary>
    /// 从磁盘读取序列化对象
    /// </summary>
    /// <param name="file"></param>
    /// <returns></returns>
    public static object? ReadObjectFromDisk(string file)
    {
        try
        {
            using Stream stream = File.Open(file, FileMode.Open);
            Console.PrintLine("Reading object from disk... ");
            var formatter = new BinaryFormatter();
            Console.PrintLine("Done.");
            return formatter.Deserialize(stream);
        }
        catch (IOException e)
        {
            Console.PrintLine("ReadObjectFromDisk()发生IO异常: {0}", e);
            LogManager.Error(e);
            return null;
        }
        catch (Exception e)
        {
            Console.PrintLine("ReadObjectFromDisk()发生异常: {0}", e);
            LogManager.Error(e);
            return null;
        }
    }
}