﻿using Microsoft.AspNet.DataProtection;
using Microsoft.Owin.Security;
using Microsoft.Owin.Security.DataHandler;
using Microsoft.Owin.Security.DataHandler.Encoder;
using System;
using System.IO;

namespace OAuth.Owin.Tokens
{

    public class TicketDataFormat : SecureDataFormat<AuthenticationTicket>
    {
        public TicketDataFormat(Microsoft.Owin.Security.DataProtection.IDataProtector protector = null) : base(
                                                                                                                  new TicketSerializer(), 
                                                                                                                  protector ?? new DataProtectorShim((new DataProtectionProvider(new DirectoryInfo(Environment.GetEnvironmentVariable("Temp", EnvironmentVariableTarget.Machine))).CreateProtector("OAuth.AspNet.AuthServer", "Access_Token", "v1"))), 
                                                                                                                  TextEncodings.Base64Url
                                                                                                              )
        {

        }
    }

}