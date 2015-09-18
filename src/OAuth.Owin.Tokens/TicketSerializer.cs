using Microsoft.Framework.Internal;
using Microsoft.Owin.Security;
using Microsoft.Owin.Security.DataHandler.Serializer;
using System;
using System.IO;
using System.Linq;
using System.Security.Claims;

namespace OAuth.Owin.Tokens
{

    public class TicketSerializer : IDataSerializer<AuthenticationTicket>
    {
        #region non-Public Members

        private const int FormatVersion = 3;

        private static void WriteWithDefault(BinaryWriter writer, string value, string defaultValue)
        {
            if (string.Equals(value, defaultValue, StringComparison.Ordinal))
            {
                writer.Write(DefaultValues.DefaultStringPlaceholder);
            }
            else
            {
                writer.Write(value);
            }
        }

        private static string ReadWithDefault(BinaryReader reader, string defaultValue)
        {
            var value = reader.ReadString();
            if (string.Equals(value, DefaultValues.DefaultStringPlaceholder, StringComparison.Ordinal))
            {
                return defaultValue;
            }
            return value;
        }

        private static class DefaultValues
        {
            public const string DefaultStringPlaceholder = "\0";
            public const string NameClaimType = "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/name";
            public const string RoleClaimType = "http://schemas.microsoft.com/ws/2008/06/identity/claims/role";
            public const string LocalAuthority = "LOCAL AUTHORITY";
            public const string StringValueType = "http://www.w3.org/2001/XMLSchema#string";
        }

        #endregion

        #region Public Members

        public virtual byte[] Serialize(AuthenticationTicket model)
        {
            using (var memory = new MemoryStream())
            {
                using (var writer = new BinaryWriter(memory))
                {
                    Write(writer, model);
                }
                return memory.ToArray();
            }
        }

        public virtual AuthenticationTicket Deserialize(byte[] data)
        {
            using (var memory = new MemoryStream(data))
            {
                using (var reader = new BinaryReader(memory))
                {
                    return Read(reader);
                }
            }
        }

        public static void Write([NotNull] BinaryWriter writer, [NotNull] AuthenticationTicket model)
        {
            writer.Write(FormatVersion);
            writer.Write(model.Identity.AuthenticationType);
            var identity = model.Identity;
            if (identity == null)
            {
                throw new ArgumentNullException(nameof(model.Identity));
            }
            else
            {
                writer.Write(1);
                var authenticationType = string.IsNullOrEmpty(identity.AuthenticationType) ? string.Empty : identity.AuthenticationType;
                writer.Write(authenticationType);
                WriteWithDefault(writer, identity.NameClaimType, DefaultValues.NameClaimType);
                WriteWithDefault(writer, identity.RoleClaimType, DefaultValues.RoleClaimType);
                writer.Write(identity.Claims.Count());

                foreach (var claim in identity.Claims)
                {
                    WriteWithDefault(writer, claim.Type, identity.NameClaimType);
                    writer.Write(claim.Value);
                    WriteWithDefault(writer, claim.ValueType, DefaultValues.StringValueType);
                    WriteWithDefault(writer, claim.Issuer, DefaultValues.LocalAuthority);
                    WriteWithDefault(writer, claim.OriginalIssuer, claim.Issuer);
                }
            }

            PropertiesSerializer.Write(writer, model.Properties);
        }

        public static AuthenticationTicket Read([NotNull] BinaryReader reader)
        {
            if (reader.ReadInt32() != FormatVersion)
            {
                return null;
            }
            var authenticationScheme = reader.ReadString();
            var identityCount = reader.ReadInt32();

            if (identityCount < 0)
            {
                return null;
            }

            var authenticationType = reader.ReadString();
            var nameClaimType = ReadWithDefault(reader, DefaultValues.NameClaimType);
            var roleClaimType = ReadWithDefault(reader, DefaultValues.RoleClaimType);
            var count = reader.ReadInt32();
            var claims = new Claim[count];
            for (int index = 0; index != count; ++index)
            {
                var type = ReadWithDefault(reader, nameClaimType);
                var value = reader.ReadString();
                var valueType = ReadWithDefault(reader, DefaultValues.StringValueType);
                var issuer = ReadWithDefault(reader, DefaultValues.LocalAuthority);
                var originalIssuer = ReadWithDefault(reader, issuer);
                claims[index] = new Claim(type, value, valueType, issuer, originalIssuer);
            }

            var identity = new ClaimsIdentity(claims, authenticationType, nameClaimType, roleClaimType);


            var properties = PropertiesSerializer.Read(reader);

            return new AuthenticationTicket(identity, properties);
        }

        #endregion
    }

}