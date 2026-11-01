// GeZi —— 多网盘下载器 (Multi-cloud downloader)
// Copyright (C) 2026  Yi Yuan
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU Affero General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU Affero General Public License for more details.
//
// You should have received a copy of the GNU Affero General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/agpl-3.0>.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Web.Script.Serialization;

namespace GeZi.Core.Api
{
    /// <summary>
    /// 各网盘 JSON 响应的通用取值工具。
    /// <para>
    /// 用框架自带的 <see cref="JavaScriptSerializer"/>（零外部依赖，可离线编译）。
    /// 网盘接口返回的字段类型经常不一致（同一个 size 有时是数字、有时是字符串），
    /// 所以这里全部做"宽容取值"：取不到就返回 null/0，绝不抛异常。
    /// </para>
    /// <para>
    /// 注意：这是**新网盘客户端专用**的工具类。夸克的 <c>QuarkClient</c> 保留自己的
    /// 私有辅助方法不动 —— 那是已经跑通的代码，没必要为了复用去改它。
    /// </para>
    /// <para>
    /// ⚠️ 可见性必须是 <c>public</c>：本项目拆成 GeZi.Core / GeZi.Providers 两个程序集后，
    /// Providers 里的四家网盘客户端都要用它。原先同程序集时写的是 internal，
    /// 拆分后会报 CS0122（"不可访问，因为它具有一定的保护级别"）。
    /// </para>
    /// </summary>
    public static class PanJson
    {
        public static object Deserialize(string json)
        {
            if (string.IsNullOrEmpty(json))
                return null;
            try
            {
                return new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(json);
            }
            catch
            {
                return null;
            }
        }

        public static string Serialize(object o)
        {
            return new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(o);
        }

        public static Dictionary<string, object> AsDict(object o)
        {
            return o as Dictionary<string, object>;
        }

        public static object[] AsArray(object o)
        {
            return o as object[];
        }

        public static object Get(Dictionary<string, object> d, string key)
        {
            if (d == null || key == null)
                return null;
            object v;
            return d.TryGetValue(key, out v) ? v : null;
        }

        /// <summary>同上，但直接接受任意对象（不是字典时返回 null）。</summary>
        public static object Get(object o, string key)
        {
            return Get(AsDict(o), key);
        }

        /// <summary>按路径逐层取值：GetPath(dict, "data", "list")。</summary>
        public static object GetPath(object root, params string[] path)
        {
            object cur = root;
            foreach (var key in path)
            {
                cur = Get(AsDict(cur), key);
                if (cur == null)
                    return null;
            }
            return cur;
        }

        public static string AsString(object o)
        {
            if (o == null)
                return null;
            var s = o as string;
            if (s != null)
                return s;
            if (o is bool)
                return (bool)o ? "true" : "false";
            return Convert.ToString(o, CultureInfo.InvariantCulture);
        }

        public static long AsLong(object o)
        {
            if (o == null)
                return 0;
            if (o is long)
                return (long)o;
            if (o is int)
                return (int)o;
            if (o is double)
                return (long)(double)o;
            if (o is decimal)
                return (long)(decimal)o;
            long parsed;
            if (long.TryParse(AsString(o), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
                return parsed;
            return 0;
        }

        public static int AsInt(object o)
        {
            var v = AsLong(o);
            if (v > int.MaxValue) return int.MaxValue;
            if (v < int.MinValue) return int.MinValue;
            return (int)v;
        }

        public static bool AsBool(object o)
        {
            if (o is bool)
                return (bool)o;
            var v = AsLong(o);
            return v != 0;
        }
    }
}