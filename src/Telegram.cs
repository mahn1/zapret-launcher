using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ZapretLauncher
{
    public class TgProxy
    {
        public string Server { get; set; }
        public int Port { get; set; }
        public string Secret { get; set; }
        public int Ms { get; set; }

        public string Host { get { return Server.TrimEnd('.') + ":" + Port; } }
        public string MsText { get { return Ms + " мс"; } }
        public string Query { get { return "server=" + Server + "&port=" + Port + "&secret=" + Secret; } }
        public string TgLink { get { return "tg://proxy?" + Query; } }
        public string WebLink { get { return "https://t.me/proxy?" + Query; } }
    }

    // Поиск MTProto-прокси для Telegram Desktop в открытых списках.
    // Трафик через MTProto-прокси зашифрован самим Telegram — владелец прокси видит только ваш IP.
    public static class Telegram
    {
        static readonly string[] Sources =
        {
            "https://raw.githubusercontent.com/SoliSpirit/mtproto/master/all_proxies.txt",
            "https://raw.githubusercontent.com/ALIILAPRO/MTProtoProxy/main/mtproto.txt",
        };

        static readonly Regex Link = new Regex(@"server=([A-Za-z0-9.\-]+)&port=(\d{1,5})&secret=([0-9A-Za-z_=\-]+)");

        public static List<TgProxy> Fetch(Action<string> log)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var all = new Dictionary<string, TgProxy>(StringComparer.OrdinalIgnoreCase);
            foreach (var src in Sources)
            {
                try
                {
                    string text;
                    using (var wc = new WebClient())
                    {
                        wc.Headers[HttpRequestHeader.UserAgent] = "ZapretLauncher";
                        text = wc.DownloadString(src);
                    }
                    int before = all.Count;
                    foreach (Match m in Link.Matches(text))
                    {
                        int port = int.Parse(m.Groups[2].Value);
                        if (port < 1 || port > 65535) continue;
                        var p = new TgProxy { Server = m.Groups[1].Value, Port = port, Secret = m.Groups[3].Value };
                        if (!all.ContainsKey(p.Host)) all[p.Host] = p;
                    }
                    log("Список " + new Uri(src).AbsolutePath.Split('/')[1] + ": +" + (all.Count - before) + " прокси");
                }
                catch (Exception e) { log("Источник недоступен: " + src + " (" + e.Message + ")"); }
            }
            return all.Values.ToList();
        }

        // ---------- проверка протоколом ----------
        // Одного TCP-подключения мало (VPN/антивирус могут принимать любые соединения локально),
        // поэтому делаем настоящее рукопожатие:
        //  • fake-TLS (секрет ee…): ClientHello, подписанный секретом; ответ сервера проверяем по HMAC —
        //    так отвечает только прокси, знающий этот секрет;
        //  • обычный/dd: обфускация obfuscated2 + MTProto req_pq_multi, ждём resPQ от Telegram.

        static readonly RandomNumberGenerator Rng = RandomNumberGenerator.Create();

        static byte[] ParseSecret(string s)
        {
            try
            {
                if (s.Length % 2 == 0 && Regex.IsMatch(s, "^[0-9a-fA-F]+$"))
                    return Enumerable.Range(0, s.Length / 2).Select(i => Convert.ToByte(s.Substring(i * 2, 2), 16)).ToArray();
                string b = s.Replace('-', '+').Replace('_', '/');
                return Convert.FromBase64String(b.PadRight(b.Length + (4 - b.Length % 4) % 4, '='));
            }
            catch { return null; }
        }

        // Время полного рукопожатия, мс; -1 — прокси не рабочий.
        public static int Check(TgProxy p, int timeoutMs)
        {
            byte[] sec = ParseSecret(p.Secret);
            if (sec == null || sec.Length < 16) return -1;
            byte[] key; string domain = null; int mode;
            if (sec.Length == 16) { key = sec; mode = 0; }
            else if (sec[0] == 0xDD && sec.Length >= 17) { key = Sub(sec, 1, 16); mode = 1; }
            else if (sec[0] == 0xEE && sec.Length > 17) { key = Sub(sec, 1, 16); domain = Encoding.ASCII.GetString(sec, 17, sec.Length - 17); mode = 2; }
            else return -1;

            using (var c = new TcpClient())
            {
                var sw = Stopwatch.StartNew();
                try
                {
                    var ar = c.BeginConnect(p.Server.TrimEnd('.'), p.Port, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(timeoutMs)) return -1;
                    c.EndConnect(ar);
                    c.ReceiveTimeout = c.SendTimeout = timeoutMs;
                    var s = c.GetStream();
                    bool ok = mode == 2 ? FakeTls(s, key, domain) : Obfuscated2(s, key, mode == 1);
                    return ok ? (int)sw.ElapsedMilliseconds : -1;
                }
                catch { return -1; }
            }
        }

        static bool FakeTls(NetworkStream s, byte[] key, string domain)
        {
            var name = Encoding.ASCII.GetBytes(domain);
            var ext = new List<byte>();
            ext.AddRange(new byte[] { 0x00, 0x00 }); ext.AddRange(U16(name.Length + 5)); ext.AddRange(U16(name.Length + 3));
            ext.Add(0x00); ext.AddRange(U16(name.Length)); ext.AddRange(name);                                      // SNI
            ext.AddRange(new byte[] { 0x00, 0x0a, 0x00, 0x04, 0x00, 0x02, 0x00, 0x1d });                            // groups: x25519
            ext.AddRange(new byte[] { 0x00, 0x0b, 0x00, 0x02, 0x01, 0x00 });                                        // ec point formats
            ext.AddRange(new byte[] { 0x00, 0x0d, 0x00, 0x08, 0x00, 0x06, 0x04, 0x03, 0x08, 0x04, 0x04, 0x01 });    // signature algs
            ext.AddRange(new byte[] { 0x00, 0x2b, 0x00, 0x03, 0x02, 0x03, 0x04 });                                  // TLS 1.3
            ext.AddRange(new byte[] { 0x00, 0x33, 0x00, 0x26, 0x00, 0x24, 0x00, 0x1d, 0x00, 0x20 }); ext.AddRange(Random(32)); // key share

            var suites = new byte[] { 0x13, 0x01, 0x13, 0x02, 0x13, 0x03, 0xc0, 0x2b, 0xc0, 0x2f, 0xc0, 0x2c, 0xc0, 0x30, 0xcc, 0xa9, 0xcc, 0xa8 };
            int fixedLen = 5 + 4 + 2 + 32 + 1 + 32 + 2 + suites.Length + 2 + 2;
            int pad = 517 - fixedLen - ext.Count - 4;   // как Telegram: ClientHello ровно 517 байт
            if (pad >= 0) { ext.AddRange(new byte[] { 0x00, 0x15 }); ext.AddRange(U16(pad)); ext.AddRange(new byte[pad]); }

            var body = new List<byte> { 0x03, 0x03 };
            body.AddRange(new byte[32]);                                   // random — заполним подписью
            body.Add(0x20); body.AddRange(Random(32));                     // session id
            body.AddRange(U16(suites.Length)); body.AddRange(suites);
            body.AddRange(new byte[] { 0x01, 0x00 });                      // compression
            body.AddRange(U16(ext.Count)); body.AddRange(ext);

            var hello = new List<byte> { 0x16, 0x03, 0x01 };
            hello.AddRange(U16(body.Count + 4));
            hello.Add(0x01); hello.Add(0x00); hello.AddRange(U16(body.Count));
            hello.AddRange(body);
            byte[] msg = hello.ToArray();

            byte[] random;
            using (var h = new HMACSHA256(key)) random = h.ComputeHash(msg);
            byte[] ts = BitConverter.GetBytes((int)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds);
            for (int i = 0; i < 4; i++) random[28 + i] ^= ts[i];
            Array.Copy(random, 0, msg, 11, 32);
            s.Write(msg, 0, msg.Length);

            // ServerHello + ChangeCipherSpec + ApplicationData
            var resp = new List<byte>();
            foreach (byte type in new byte[] { 0x16, 0x14, 0x17 })
            {
                byte[] head = ReadExact(s, 5);
                if (head[0] != type || head[1] != 0x03) return false;
                int len = head[3] << 8 | head[4];
                resp.AddRange(head); resp.AddRange(ReadExact(s, len));
            }
            byte[] r = resp.ToArray();
            byte[] serverRandom = Sub(r, 11, 32);
            Array.Clear(r, 11, 32);
            byte[] expect;
            using (var h = new HMACSHA256(key)) expect = h.ComputeHash(random.Concat(r).ToArray());
            return expect.SequenceEqual(serverRandom);
        }

        static bool Obfuscated2(NetworkStream s, byte[] key, bool padded)
        {
            byte[] init;
            while (true)
            {
                init = Random(64);
                uint first = BitConverter.ToUInt32(init, 0);
                if (init[0] == 0xef || BitConverter.ToUInt32(init, 4) == 0) continue;
                if (first == 0x44414548 || first == 0x54534f50 || first == 0x20544547 || first == 0x4954504f
                    || first == 0xdddddddd || first == 0xeeeeeeee || first == 0x02010316) continue;
                break;
            }
            byte tag = padded ? (byte)0xdd : (byte)0xee;   // padded intermediate / intermediate
            for (int i = 56; i < 60; i++) init[i] = tag;
            init[60] = 2; init[61] = 0;                      // DC 2

            byte[] rev = Sub(init, 8, 48).Reverse().ToArray();
            var enc = new Ctr(Sha(Sub(init, 8, 32), key), Sub(init, 40, 16));
            var dec = new Ctr(Sha(Sub(rev, 0, 32), key), Sub(rev, 32, 16));
            byte[] encInit = enc.Process(init);
            Array.Copy(encInit, 56, init, 56, 8);
            s.Write(init, 0, 64);

            // незашифрованное MTProto-сообщение req_pq_multi
            var m = new List<byte>(new byte[8]);
            long msgId = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds << 32;
            m.AddRange(BitConverter.GetBytes(msgId));
            m.AddRange(BitConverter.GetBytes(20));
            m.AddRange(BitConverter.GetBytes(0xbe7e8ef1));
            m.AddRange(Random(16));
            var packet = BitConverter.GetBytes(m.Count).Concat(m).ToArray();
            byte[] e = enc.Process(packet);
            s.Write(e, 0, e.Length);

            int len = BitConverter.ToInt32(dec.Process(ReadExact(s, 4)), 0);
            if (len < 24 || len > 4096) return false;
            byte[] answer = dec.Process(ReadExact(s, len));
            return BitConverter.ToUInt32(answer, 20) == 0x05162463;   // resPQ
        }

        class Ctr
        {
            readonly ICryptoTransform ecb;
            readonly byte[] counter, stream = new byte[16];
            int pos = 16;

            public Ctr(byte[] key, byte[] iv)
            {
                var aes = Aes.Create();
                aes.Mode = CipherMode.ECB; aes.Padding = PaddingMode.None; aes.Key = key;
                ecb = aes.CreateEncryptor();
                counter = (byte[])iv.Clone();
            }

            public byte[] Process(byte[] data)
            {
                var r = new byte[data.Length];
                for (int i = 0; i < data.Length; i++)
                {
                    if (pos == 16)
                    {
                        ecb.TransformBlock(counter, 0, 16, stream, 0);
                        for (int k = 15; k >= 0 && ++counter[k] == 0; k--) { }
                        pos = 0;
                    }
                    r[i] = (byte)(data[i] ^ stream[pos++]);
                }
                return r;
            }
        }

        static byte[] ReadExact(Stream s, int n)
        {
            var b = new byte[n];
            for (int got = 0; got < n; )
            {
                int k = s.Read(b, got, n - got);
                if (k <= 0) throw new IOException("соединение закрыто");
                got += k;
            }
            return b;
        }

        static byte[] Sub(byte[] a, int from, int len) { var r = new byte[len]; Array.Copy(a, from, r, 0, len); return r; }
        static byte[] U16(int v) { return new[] { (byte)(v >> 8), (byte)v }; }
        static byte[] Random(int n) { var b = new byte[n]; Rng.GetBytes(b); return b; }
        static byte[] Sha(byte[] a, byte[] b) { using (var h = SHA256.Create()) return h.ComputeHash(a.Concat(b).ToArray()); }

        // Проверяет все прокси параллельно (до 64 одновременно), возвращает рабочие по возрастанию отклика.
        public static async Task<List<TgProxy>> CheckAll(List<TgProxy> list, Action<int, int> progress)
        {
            int w, io;
            ThreadPool.GetMinThreads(out w, out io);
            if (w < 72) ThreadPool.SetMinThreads(72, io);
            var gate = new SemaphoreSlim(64);
            int done = 0, alive = 0;
            var tasks = list.Select(async p =>
            {
                await gate.WaitAsync();
                try
                {
                    p.Ms = await Task.Run(() => Check(p, 4000));
                    if (p.Ms >= 0) Interlocked.Increment(ref alive);
                }
                finally
                {
                    gate.Release();
                    progress(Interlocked.Increment(ref done), alive);
                }
            });
            await Task.WhenAll(tasks);
            return list.Where(p => p.Ms >= 0).OrderBy(p => p.Ms).ToList();
        }

        public static bool Running()
        {
            var ps = Process.GetProcessesByName("Telegram");
            foreach (var p in ps) p.Dispose();
            return ps.Length > 0;
        }

        public static string FindExe()
        {
            foreach (var p in Process.GetProcessesByName("Telegram"))
            {
                try { return p.MainModule.FileName; } catch { }
                finally { p.Dispose(); }
            }
            string def = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Telegram Desktop", "Telegram.exe");
            return File.Exists(def) ? def : null;
        }
    }
}
