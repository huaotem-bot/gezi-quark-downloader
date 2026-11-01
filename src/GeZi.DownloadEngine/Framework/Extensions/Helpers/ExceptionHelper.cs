using System;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;

namespace GeZi.DownloadEngine.Framework.Extensions.Helpers;

internal static class ExceptionHelper
{
    internal static bool IsMomentumError(this Exception error)
    {
        if (error.HasSource("System.Net.Http",
            "System.Net.Sockets",
            "System.Net.Security"))
            return true;

        if (error.HasTypeOf(typeof(WebException), typeof(SocketException)))
            return true;

        return false;
    }

    internal static bool HasTypeOf(this Exception exp, params Type[] types)
    {
        Exception innerException = exp;
        while (innerException != null)
        {
            foreach (Type type in types)
            {
                if (innerException.GetType() == type)
                    return true;
            }

            innerException = innerException.InnerException;
        }

        return false;
    }

    internal static bool HasSource(this Exception exp, params string[] sources)
    {
        Exception innerException = exp;
        while (innerException != null)
        {
            foreach (string source in sources)
            {
                if (string.Equals(innerException.Source, source, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            innerException = innerException.InnerException;
        }

        return false;
    }
}
