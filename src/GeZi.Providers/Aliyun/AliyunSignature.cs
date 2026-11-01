using System;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using GeZi.Core.Api;
using GeZi.Core.Models;
using GeZi.Core.Support;

namespace GeZi.Providers.Aliyun
{
    /// <summary>阿里云盘设备签名的结果。</summary>
    public sealed class AliyunSignatureResult
    {
        /// <summary>紧凑签名：r(64 hex) + s(64 hex) + recovery(2 hex)。</summary>
        public string Signature;

        /// <summary>未压缩公钥：04 + x(64 hex) + y(64 hex)。</summary>
        public string PublicKey;
    }

    /// <summary>
    /// 阿里云盘网页版「设备会话」签名（secp256k1）。
    /// <para>
    /// 协议来源：refs/wenxi/lib/data/providers/aliyun_signature.dart（Flutter 实现，实测可用）。
    /// 摘要 = sha256("{appId}:{deviceId}:{userId}:{nonce}")，
    /// 输出紧凑签名 = r(64 hex) + s(64 hex) + recovery(2 hex)，
    /// 公钥用未压缩格式（04 || x || y）。
    /// </para>
    /// <para>
    /// 为什么需要它：阿里云盘新版对 <c>/adrive/v3/file/list</c>、容量查询等接口
    /// 强制要求设备会话签名（服务端会返回 DeviceSessionSignatureInvalid /
    /// InvalidDeviceSession）。缺了签名，access_token 再对也列不出文件 ——
    /// 这正是「能登录、能看到昵称，但我的网盘一片空白」的根因。
    /// </para>
    /// <para>
    /// 实现策略：ECDSA 运算交给 .NET 自带的 <see cref="ECDsaCng"/>（Windows CNG
    /// 原生支持 secp256k1，实测 CreateFromFriendlyName 可用），
    /// 只用 <see cref="BigInteger"/> 补两件 CNG 不直接给的东西：
    /// 从私钥推导公钥点、以及紧凑签名要求的 recovery id。
    /// 这样不必手写曲线运算，正确性由系统密码库保证。
    /// </para>
    /// </summary>
    public static class AliyunSignature
    {
        /// <summary>阿里网页版前端固定的 appId（签名摘要前缀）。</summary>
        public const string AppId = "5dde4e1bdf9e4966b387ba58f4b3fdc3";

        /// <summary>secp256k1 的素数模 p。</summary>
        private static readonly BigInteger P = ParseHex(
            "FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFEFFFFFC2F");

        /// <summary>secp256k1 的基点阶 n。</summary>
        private static readonly BigInteger N = ParseHex(
            "FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFEBAAEDCE6AF48A03BBFD25E8CD0364141");

        /// <summary>基点 G 的 x 坐标。</summary>
        private static readonly BigInteger Gx = ParseHex(
            "79BE667EF9DCBBAC55A06295CE870B07029BFCDB2DCE28D959F2815B16F81798");

        /// <summary>基点 G 的 y 坐标。</summary>
        private static readonly BigInteger Gy = ParseHex(
            "483ADA7726A3C4655DA4FBFC0E1108A8FD17B448A68554199C47D08FFB10D4B8");

        /// <summary>n/2，用于把 s 规范化到 low-s。</summary>
        private static readonly BigInteger HalfN = N >> 1;

        private static ECCurve Curve
        {
            get { return ECCurve.CreateFromFriendlyName("secp256k1"); }
        }

        /// <summary>
        /// 生成一个新的 64 位十六进制私钥（范围 1 ≤ d &lt; n）。
        /// 只在首次为该账号创建会话时调用，之后应持久化复用 ——
        /// 换私钥等于换设备，服务端会要求重新 create_session。
        /// </summary>
        public static string NewPrivateKeyHex()
        {
            using (var rng = RandomNumberGenerator.Create())
            {
                var buf = new byte[32];
                while (true)
                {
                    rng.GetBytes(buf);
                    BigInteger d = ToBigInteger(buf, 0, buf.Length);
                    if (d.Sign > 0 && d < N)
                        return Hex(d, 32);
                }
            }
        }

        /// <summary>
        /// 从私钥推导未压缩公钥（04 || x || y）。
        /// 用于 ImportParameters 与持久化 —— CNG 的 ImportParameters 要求同时给 D 和 Q。
        /// </summary>
        public static string DerivePublicKeyHex(string privateKeyHex)
        {
            BigInteger d = RequirePrivateKey(privateKeyHex);
            Point pub = Multiply(d, BasePoint());
            if (pub.IsInfinity)
                throw new InvalidOperationException("阿里设备公钥推导失败。");
            return "04" + Hex(pub.X, 32) + Hex(pub.Y, 32);
        }

        /// <summary>
        /// 计算设备签名。
        /// </summary>
        /// <param name="privateKeyHex">64 位十六进制私钥。</param>
        /// <param name="deviceId">设备 ID（32 位十六进制，本地生成后持久化）。</param>
        /// <param name="userId">阿里账号 user_id（从 /user/get 取）。</param>
        /// <param name="nonce">nonce，默认 0（与官方前端一致）。</param>
        public static AliyunSignatureResult Sign(string privateKeyHex, string deviceId,
            string userId, int nonce = 0)
        {
            BigInteger d = RequirePrivateKey(privateKeyHex);
            if (string.IsNullOrEmpty(deviceId))
                throw new ArgumentException("阿里设备 ID 不能为空。", "deviceId");
            if (string.IsNullOrEmpty(userId))
                throw new ArgumentException("阿里账号 user_id 不能为空。", "userId");

            Point pub = Multiply(d, BasePoint());
            if (pub.IsInfinity)
                throw new InvalidOperationException("阿里设备公钥推导失败。");

            byte[] digest = Digest(deviceId, userId, nonce);

            byte[] raw;
            using (var ecdsa = ECDsaCng.Create())
            {
                ecdsa.ImportParameters(new ECParameters
                {
                    Curve = Curve,
                    D = ToBigEndian(d, 32),
                    Q = new ECPoint
                    {
                        X = ToBigEndian(pub.X, 32),
                        Y = ToBigEndian(pub.Y, 32),
                    },
                });
                // ⚠️ 必须用 SignHash 而不是 SignData(digest, SHA256)：
                //    digest 已经是 sha256(appId:deviceId:userId:nonce) 的结果，
                //    SignData 会再哈希一次，而 recovery id 是按 e = digest 推的 ——
                //    两者不一致时签名本身「合法」但 recovery 恢复出错误的公钥，
                //    服务端只会回一句 InvalidDeviceSession。本轮的 SelfCheck 正是靠这条抓出来的。
                raw = ecdsa.SignHash(digest);
            }

            if (raw == null || raw.Length != 64)
                throw new InvalidOperationException("阿里设备签名长度异常："
                    + (raw == null ? "null" : raw.Length.ToString(CultureInfo.InvariantCulture)));

            BigInteger r = ToBigInteger(raw, 0, 32);
            BigInteger s = ToBigInteger(raw, 32, 32);

            // 规范化到 low-s：紧凑签名格式的通行约定。
            // s → n-s 等价于对签名点取负，因此下面的 recovery 用最终 s 计算，保持一致。
            if (s > HalfN)
                s = N - s;

            int recovery = ComputeRecovery(r, s, digest, pub);

            return new AliyunSignatureResult
            {
                Signature = Hex(r, 32) + Hex(s, 32)
                    + recovery.ToString("x2", CultureInfo.InvariantCulture),
                PublicKey = "04" + Hex(pub.X, 32) + Hex(pub.Y, 32),
            };
        }

        /// <summary>
        /// 自检：生成临时密钥 → 签名 → 用公钥验签 → 从 (r,s,recovery) 反推公钥并与原公钥比对。
        /// 供 SelfTest 调用，防回归 —— recovery id 算错时服务端只会回一个
        /// InvalidDeviceSession，很难从错误信息反推，所以必须在这里钉死。
        /// </summary>
        public static bool SelfCheck(out string detail)
        {
            try
            {
                string d = NewPrivateKeyHex();
                string pubHex = DerivePublicKeyHex(d);
                const string deviceId = "00112233445566778899aabbccddeeff";
                const string userId = "0123456789abcdef0123456789abcdef";

                AliyunSignatureResult result = Sign(d, deviceId, userId, 0);

                if (result.Signature == null || result.Signature.Length != 130)
                {
                    detail = "签名长度应为 130，实际 "
                        + (result.Signature == null ? "null"
                            : result.Signature.Length.ToString(CultureInfo.InvariantCulture));
                    return false;
                }
                if (result.PublicKey != pubHex)
                {
                    detail = "Sign 返回的公钥与 DerivePublicKeyHex 不一致";
                    return false;
                }

                byte[] digest = Digest(deviceId, userId, 0);
                var sigBytes = new byte[64];
                for (int i = 0; i < 32; i++)
                {
                    sigBytes[i] = Convert.ToByte(result.Signature.Substring(i * 2, 2), 16);
                    sigBytes[32 + i] = Convert.ToByte(result.Signature.Substring(64 + i * 2, 2), 16);
                }

                using (var ecdsa = ECDsaCng.Create())
                {
                    ecdsa.ImportParameters(new ECParameters
                    {
                        Curve = Curve,
                        Q = new ECPoint
                        {
                            X = HexToBytes(pubHex.Substring(2, 64)),
                            Y = HexToBytes(pubHex.Substring(66, 64)),
                        },
                    });
                    if (!ecdsa.VerifyHash(digest, sigBytes))
                    {
                        detail = "公钥验签失败";
                        return false;
                    }
                }

                BigInteger rr = ToBigInteger(sigBytes, 0, 32);
                BigInteger ss = ToBigInteger(sigBytes, 32, 32);
                int recovery = Convert.ToInt32(result.Signature.Substring(128, 2), 16);
                Point recovered = RecoverPublicKey(rr, ss, recovery, digest);
                string recoveredHex = "04" + Hex(recovered.X, 32) + Hex(recovered.Y, 32);
                if (recoveredHex != pubHex)
                {
                    detail = "recovery id 恢复的公钥与原公钥不一致";
                    return false;
                }

                detail = "签名 130 hex / 公钥验签通过 / recovery 恢复一致";
                return true;
            }
            catch (Exception ex)
            {
                detail = ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }

        /// <summary>
        /// 从 (r, s, recovery) 反推公钥点：Q = r⁻¹(s·R − e·G)。
        /// 仅用于自检，正常签名流程不调用。
        /// </summary>
        private static Point RecoverPublicKey(BigInteger r, BigInteger s, int recovery, byte[] digest)
        {
            BigInteger x = r;
            if ((recovery & 2) != 0)
                x += N;
            if (x >= P)
                throw new InvalidOperationException("recovery 的 x 超出域范围。");

            BigInteger alpha = Mod(x * x * x + 7, P);
            // p ≡ 3 (mod 4) → 平方根 = alpha^((p+1)/4)
            BigInteger beta = BigInteger.ModPow(alpha, (P + 1) >> 2, P);
            if (Mod(beta * beta, P) != alpha)
                throw new InvalidOperationException("recovery 的 x 不在曲线上。");

            BigInteger y = (beta.IsEven ? 0 : 1) == (recovery & 1) ? beta : Mod(P - beta, P);
            var pointR = new Point { X = x, Y = y };

            BigInteger e = ToBigInteger(digest, 0, digest.Length);
            BigInteger rInv = ModInverse(r, N);

            Point sR = Multiply(s, pointR);
            Point eG = Multiply(e, BasePoint());
            Point negEG = eG.IsInfinity
                ? Infinity()
                : new Point { X = eG.X, Y = Mod(P - eG.Y, P) };

            return Multiply(rInv, Add(sR, negEG));
        }

        private static byte[] HexToBytes(string hex)
        {
            var bytes = new byte[hex.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
                bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            return bytes;
        }

        /// <summary>签名摘要：sha256("{appId}:{deviceId}:{userId}:{nonce}")。</summary>
        private static byte[] Digest(string deviceId, string userId, int nonce)
        {
            string message = AppId + ":" + deviceId + ":" + userId + ":"
                + nonce.ToString(CultureInfo.InvariantCulture);
            using (var sha = SHA256.Create())
                return sha.ComputeHash(Encoding.UTF8.GetBytes(message));
        }

        /// <summary>
        /// 计算 recovery id：R = u1·G + u2·Q，取 R.y 的奇偶位，
        /// 若 R.x ≥ n 再置第 2 位。与 wenxi 的
        /// <c>(point.y.isOdd ? 1 : 0) | (point.x &gt;= n ? 2 : 0)</c> 等价。
        /// </summary>
        private static int ComputeRecovery(BigInteger r, BigInteger s, byte[] digest, Point pub)
        {
            BigInteger e = ToBigInteger(digest, 0, digest.Length);
            BigInteger w = ModInverse(s, N);
            BigInteger u1 = Mod(e * w, N);
            BigInteger u2 = Mod(r * w, N);

            Point point = Add(Multiply(u1, BasePoint()), Multiply(u2, pub));
            if (point.IsInfinity)
                return 0;

            int bit = point.Y.IsEven ? 0 : 1;
            if (point.X >= N)
                bit |= 2;
            return bit;
        }

        private static Point BasePoint()
        {
            return new Point { X = Gx, Y = Gy };
        }

        private static BigInteger RequirePrivateKey(string privateKeyHex)
        {
            if (!IsHex64(privateKeyHex))
                throw new ArgumentException("阿里设备私钥格式无效（需 64 位十六进制）。",
                    "privateKeyHex");
            BigInteger d = ParseHex(privateKeyHex);
            if (d.Sign <= 0 || d >= N)
                throw new ArgumentException("阿里设备私钥超出曲线阶范围。", "privateKeyHex");
            return d;
        }

        private static bool IsHex64(string s)
        {
            if (s == null || s.Length != 64)
                return false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                bool ok = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!ok)
                    return false;
            }
            return true;
        }

        // ---------------- secp256k1 仿射坐标点运算 ----------------

        private struct Point
        {
            public BigInteger X;
            public BigInteger Y;
            public bool IsInfinity;
        }

        private static Point Infinity()
        {
            return new Point { IsInfinity = true };
        }

        private static Point Double(Point p)
        {
            if (p.IsInfinity)
                return p;
            if (p.Y.IsZero)
                return Infinity();

            // a = 0 → λ = 3x² / 2y
            BigInteger lambda = Mod(
                Mod(3 * p.X * p.X, P) * ModInverse(Mod(2 * p.Y, P), P), P);
            BigInteger x3 = Mod(lambda * lambda - 2 * p.X, P);
            BigInteger y3 = Mod(lambda * (p.X - x3) - p.Y, P);
            return new Point { X = x3, Y = y3 };
        }

        private static Point Add(Point p, Point q)
        {
            if (p.IsInfinity)
                return q;
            if (q.IsInfinity)
                return p;

            if (p.X == q.X)
            {
                // 同 x：要么是同点（倍点），要么互为逆元（y 相加为 0 → 无穷远）
                if (Mod(p.Y + q.Y, P).IsZero)
                    return Infinity();
                return Double(p);
            }

            BigInteger lambda = Mod(
                Mod(q.Y - p.Y, P) * ModInverse(Mod(q.X - p.X, P), P), P);
            BigInteger x3 = Mod(lambda * lambda - p.X - q.X, P);
            BigInteger y3 = Mod(lambda * (p.X - x3) - p.Y, P);
            return new Point { X = x3, Y = y3 };
        }

        private static Point Multiply(BigInteger k, Point p)
        {
            if (p.IsInfinity || k.IsZero)
                return Infinity();

            k = Mod(k, N);
            if (k.IsZero)
                return Infinity();

            Point result = Infinity();
            Point addend = p;
            while (k > 0)
            {
                if (!(k & BigInteger.One).IsZero)
                    result = Add(result, addend);
                addend = Double(addend);
                k >>= 1;
            }
            return result;
        }

        private static BigInteger Mod(BigInteger value, BigInteger m)
        {
            BigInteger r = value % m;
            return r.Sign < 0 ? r + m : r;
        }

        /// <summary>扩展欧几里得求模逆。</summary>
        private static BigInteger ModInverse(BigInteger value, BigInteger m)
        {
            BigInteger a = Mod(value, m);
            if (a.IsZero)
                throw new InvalidOperationException("模逆不存在：被除数为 0。");

            BigInteger oldR = a, r = m;
            BigInteger oldS = BigInteger.One, s = BigInteger.Zero;
            while (!r.IsZero)
            {
                BigInteger q = oldR / r;
                BigInteger tmp = oldR - q * r;
                oldR = r;
                r = tmp;
                tmp = oldS - q * s;
                oldS = s;
                s = tmp;
            }
            if (!oldR.IsOne)
                throw new InvalidOperationException("模逆不存在：两数不互素。");
            return Mod(oldS, m);
        }

        // ---------------- 字节序辅助（BigInteger 是 little-endian） ----------------

        private static BigInteger ParseHex(string hex)
        {
            if (string.IsNullOrEmpty(hex))
                return BigInteger.Zero;
            if ((hex.Length & 1) == 1)
                hex = "0" + hex;
            var be = new byte[hex.Length / 2];
            for (int i = 0; i < be.Length; i++)
                be[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            return ToBigInteger(be, 0, be.Length);
        }

        /// <summary>
        /// 大端字节 → BigInteger。
        /// <para>
        /// ⚠️ 必须多补一个 0x00 高位字节：<see cref="BigInteger(byte[])"/> 把最后一个字节
        /// 当作符号位，而曲线参数（p、n、Gx、Gy）最高位都是 1 —— 直接构造会把它们读成**负数**，
        /// 于是 <c>d &lt; N</c> 永远为假、<c>Mod</c> 全部算错。
        /// 这个坑在本轮实测中让密钥生成直接死循环。
        /// </para>
        /// </summary>
        private static BigInteger ToBigInteger(byte[] bigEndian, int offset, int count)
        {
            var le = new byte[count + 1];
            for (int i = 0; i < count; i++)
                le[i] = bigEndian[offset + count - 1 - i];
            // le[count] 保持 0 → 强制按正数解释
            return new BigInteger(le);
        }

        private static byte[] ToBigEndian(BigInteger value, int length)
        {
            if (value.Sign < 0)
                throw new ArgumentOutOfRangeException("value");

            byte[] le = value.ToByteArray();
            int used = le.Length;
            while (used > 0 && le[used - 1] == 0)
                used--;
            if (used > length)
                throw new ArgumentException("数值超出目标字节长度。", "length");

            var be = new byte[length];
            for (int i = 0; i < used; i++)
                be[length - 1 - i] = le[i];
            return be;
        }

        private static string Hex(BigInteger value, int bytes)
        {
            byte[] be = ToBigEndian(value, bytes);
            var sb = new StringBuilder(bytes * 2);
            for (int i = 0; i < be.Length; i++)
                sb.Append(be[i].ToString("x2", CultureInfo.InvariantCulture));
            return sb.ToString();
        }
    }
}
