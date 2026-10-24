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

namespace GeZi.Core.Api
{
    /// <summary>夸克接口的业务错误，携带原始 status/code 以便上层分级处理。</summary>
    public class QuarkApiException : Exception
    {
        public object Code { get; }
        public object Status { get; }
        /// <summary>原始 HTTP 状态码（如 401/412/429），与 JSON 中的 status 区分。</summary>
        public int? HttpStatus { get; set; }

        public QuarkApiException(string message, object code = null, object status = null, int? httpStatus = null)
            : base(message)
        {
            Code = code;
            Status = status;
            HttpStatus = httpStatus;
        }
    }
}
