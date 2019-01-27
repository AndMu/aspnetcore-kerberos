﻿using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication.GssKerberos.Disposables;

using static Microsoft.AspNetCore.Authentication.GssKerberos.Native.Krb5Interop;

namespace Microsoft.AspNetCore.Authentication.GssKerberos
{
    public class GssAcceptor : IAcceptor
    {
        private readonly IntPtr _acceptorCredentials;
        private IntPtr _context;
        private IntPtr _sourceName;
        private uint _flags;
        private uint _expiryTime;

        public bool IsEstablished { get; private set; }

        /// <summary>
        /// The UPN of the context initiator
        /// </summary>
        public string Principal { get; private set; }

        /// <summary>
        /// The logon-info
        /// </summary>
        internal byte[] Pac { get; private set; }

        /// <summary>
        /// The final negotiated flags
        /// </summary>
        public uint Flags { get; private set; }

        public GssAcceptor(GssCredential credential) => 
            _acceptorCredentials = credential.Credentials;

        public byte[] Accept(byte[] token)
        {
            using (var inputBuffer = GssBuffer.FromBytes(token))
            {
                // decrypt and verify the incoming service ticket
                var majorStatus = gss_accept_sec_context(
                    out var minorStatus,
                    ref _context,
                    _acceptorCredentials,
                    ref inputBuffer.Value,
                    IntPtr.Zero,        // no support for channel binding
                    out _sourceName,
                    ref GssSpnegoMechOidDesc,
                    out var output,
                    out _flags, out _expiryTime, IntPtr.Zero
                );

                switch (majorStatus)
                {
                    case GSS_S_COMPLETE:
                        CompleteContext(_sourceName);
                        return MarshalOutputToken(output);

                    case GSS_S_CONTINUE_NEEDED:
                        return MarshalOutputToken(output);

                    default:
                        throw new GssException("The GSS Provider was unable to accept the supplied authentication token",
                            majorStatus, minorStatus, GssSpnegoMechOidDesc);
                }
            }
        }

        private static byte[] MarshalOutputToken(GssBufferStruct gssToken)
        {
            if (gssToken.length > 0)
            {
                // copy the output token to a managed buffer and release the gss buffer
                var buffer = new byte[gssToken.length];
                Marshal.Copy(gssToken.value, buffer, 0, (int)gssToken.length);

                var majorStatus = gss_release_buffer(out var minorStatus, ref gssToken);
                if (majorStatus != GSS_S_COMPLETE)
                    throw new GssException("An error occurred releasing the token buffer allocated by the GSS provider",
                        majorStatus, minorStatus, GssSpnegoMechOidDesc);

                return buffer;
            }
            return new byte[0];
        }

        private void CompleteContext(IntPtr sourceName)
        {
            // Use GSS to translate the opaque name to an ASCII 'display' name
            var majorStatus = gss_display_name(
                out var minorStatus,
                sourceName,
                out var nameBuffer,
                out var nameType);

            if (majorStatus != GSS_S_COMPLETE)
                throw new GssException("An error occurred getting the display name of the principal",
                    majorStatus, minorStatus, GssSpnegoMechOidDesc);

            // Set the context properties on the acceptor
            Flags = _flags;
            IsEstablished = true;
            Principal = Marshal.PtrToStringAnsi(nameBuffer.value, (int)nameBuffer.length);

            // release the GSS allocated buffers
            majorStatus = gss_release_buffer(out minorStatus, ref nameBuffer);
            if (majorStatus != GSS_S_COMPLETE)
                throw new GssException("An error occurred releasing the display name of the principal",
                    majorStatus, minorStatus, GssSpnegoMechOidDesc);

            // The Windows AD-WIN2K-PAC certificate is located in the Authzdata we can get the raw authzdata and parse it, looking for the PAC
            // ...or use the preferred krb5_gss_get_name_attribute("urn:mspac:")
            using (var inputBuffer = GssBuffer.FromString("urn:mspac:logon-info"))
            {
                var hasMore = -1;
                majorStatus = gss_get_name_attribute(out minorStatus,
                    sourceName,
                    ref inputBuffer.Value,
                    out var authenticated,
                    out var complete,
                    out var value,
                    out var displayValue,
                    ref hasMore);
                
                if (majorStatus != GSS_S_COMPLETE)
                    throw new GssException("An error occurred obtaining the Windows PAC data from the Kerberos Ticket",
                        majorStatus, minorStatus, GssSpnegoMechOidDesc);
                
                // Allocate a clr byte arry and copy the Pac data over
                Pac = new byte[value.length];
                Marshal.Copy(value.value, Pac, 0, (int)value.length);

                // TODO: decode the structure, we can extract group membership information (SID's) from the PAC
                AsnEncodedData d = new AsnEncodedData(Pac);
                var data = d.Format(true);
            }
        }
        
        public void Dispose()
        {
            var majorStatus = gss_delete_sec_context(out var minorStatus, ref _context);
            if (majorStatus != GSS_S_COMPLETE)
                throw new GssException("The GSS provider returned an error while attempting to delete the GSS Context",
                    majorStatus, minorStatus, GssSpnegoMechOidDesc);
        }
    }
}
