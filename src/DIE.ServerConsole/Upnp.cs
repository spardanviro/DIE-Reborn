using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;

namespace DIE.ServerConsole
{
    /// <summary>
    /// 家里开服时让路由器自动做端口映射 (UPnP, Windows 自带的 HNetCfg.NATUPnP)。
    ///
    /// 只是"尽力而为": 路由器没开 UPnP、光猫还套着一层路由、运营商没给公网 IP 的, 都做不成 ——
    /// 那时只能手动在路由器里映射, 或者用云服务器。云服务器用不着这个 (它没有路由器, 是安全组)。
    /// </summary>
    internal static class Upnp
    {
        public const string Description = "DIE_Reborn";

        public sealed class Outcome
        {
            public bool Supported { get; set; }
            public string LocalAddress { get; set; }
            public int Mapped { get; set; }
            public int Failed { get; set; }
        }

        /// <param name="ports">(协议 "TCP"/"UDP", 端口) —— 外部端口和内部端口一样。</param>
        public static Outcome Map(IEnumerable<(string Protocol, int Port)> ports)
        {
            Outcome outcome = new Outcome { LocalAddress = LocalAddress() };
            if (outcome.LocalAddress == null)
            {
                return outcome;
            }

            Type type = Type.GetTypeFromProgID("HNetCfg.NATUPnP", throwOnError: false);
            if (type == null)
            {
                return outcome;
            }

            object nat = Activator.CreateInstance(type);
            object mappings = null;
            try
            {
                // 找不到支持 UPnP 的路由器时是 null (或者等几秒后抛异常)
                mappings = Invoke(nat, "StaticPortMappingCollection", BindingFlags.GetProperty);
                if (mappings == null)
                {
                    return outcome;
                }

                outcome.Supported = true;
                foreach ((string protocol, int port) in ports)
                {
                    try
                    {
                        object mapping = Invoke(mappings, "Add", BindingFlags.InvokeMethod,
                            port, protocol, port, outcome.LocalAddress, true, Description);
                        if (mapping != null)
                        {
                            Marshal.ReleaseComObject(mapping);
                        }

                        outcome.Mapped++;
                    }
                    catch (Exception ex) when (ex is TargetInvocationException || ex is COMException)
                    {
                        outcome.Failed++;
                    }
                }
            }
            catch (Exception ex) when (ex is TargetInvocationException || ex is COMException)
            {
                outcome.Supported = false;
            }
            finally
            {
                if (mappings != null)
                {
                    Marshal.ReleaseComObject(mappings);
                }

                Marshal.ReleaseComObject(nat);
            }

            return outcome;
        }

        private static object Invoke(object target, string name, BindingFlags flags, params object[] args) =>
            target.GetType().InvokeMember(name, flags, null, target, args);

        /// <summary>本机连外网时用的那个内网地址 (UDP "连接"不发包, 只是让系统选路由)。</summary>
        public static string LocalAddress()
        {
            try
            {
                using (Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
                {
                    socket.Connect("8.8.8.8", 53);
                    return ((IPEndPoint)socket.LocalEndPoint).Address.ToString();
                }
            }
            catch (SocketException)
            {
                return null;
            }
        }
    }
}
