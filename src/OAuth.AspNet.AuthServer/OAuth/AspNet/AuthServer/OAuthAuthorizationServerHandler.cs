﻿using Microsoft.AspNet.Authentication;
using Microsoft.AspNet.Http;
using Microsoft.AspNet.Http.Authentication;
using Microsoft.AspNet.Http.Features.Authentication;
using Microsoft.AspNet.WebUtilities;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace OAuth.AspNet.AuthServer
{

    public class OAuthAuthorizationServerHandler : AuthenticationHandler<OAuthAuthorizationServerOptions>
    {
        #region non-Public Members

        private AuthorizeEndpointRequest _authorizeEndpointRequest;

        private OAuthValidateClientRedirectUriContext _clientContext;

        private Task SendErrorAsJsonAsync(BaseValidatingContext<OAuthAuthorizationServerOptions> validatingContext)
        {
            string error = validatingContext.HasError ? validatingContext.Error : Constants.Errors.InvalidRequest;
            string errorDescription = validatingContext.HasError ? validatingContext.ErrorDescription : null;
            string errorUri = validatingContext.HasError ? validatingContext.ErrorUri : null;

            string body;

            MemoryStream stream, memoryStream = null;

            StreamWriter streamWriter = null;

            try
            {
                stream = memoryStream = new MemoryStream();

                streamWriter = new StreamWriter(memoryStream);

                using (var writer = new JsonTextWriter(streamWriter))
                {
                    memoryStream = null;

                    streamWriter = null;

                    writer.WriteStartObject();
                    writer.WritePropertyName(Constants.Parameters.Error);
                    writer.WriteValue(error);
                    if (!string.IsNullOrEmpty(errorDescription))
                    {
                        writer.WritePropertyName(Constants.Parameters.ErrorDescription);
                        writer.WriteValue(errorDescription);
                    }
                    if (!string.IsNullOrEmpty(errorUri))
                    {
                        writer.WritePropertyName(Constants.Parameters.ErrorUri);
                        writer.WriteValue(errorUri);
                    }
                    writer.WriteEndObject();
                    writer.Flush();
                    body = Encoding.UTF8.GetString(stream.ToArray());
                }
            }
            finally
            {
                if (memoryStream != null)
                    memoryStream.Dispose();
            }

            Response.StatusCode = 400;
            Response.ContentType = "application/json;charset=UTF-8";
            Response.Headers["Cache-Control"] = "no-cache";
            Response.Headers["Pragma"] = "no-cache";
            Response.Headers["Expires"] = "-1";
            Response.Headers["Content-Length"] = body.Length.ToString(CultureInfo.InvariantCulture);
            return Response.WriteAsync(body, Context.RequestAborted);
        }

        private async Task<bool> SendErrorPageAsync(string error, string errorDescription, string errorUri)
        {
            Response.StatusCode = 400;
            Response.Headers["Cache-Control"] = "no-cache";
            Response.Headers["Pragma"] = "no-cache";
            Response.Headers["Expires"] = "-1";

            if (Options.ApplicationCanDisplayErrors)
            {
                Context.Items["oauth.Error"] = error;
                Context.Items["oauth.ErrorDescription"] = errorDescription;
                Context.Items["oauth.ErrorUri"] = errorUri;

                // request is not handled - pass through to application for rendering
                return false;
            }

            var memory = new MemoryStream();
            string body;
            using (var writer = new StreamWriter(memory))
            {
                writer.WriteLine("error: {0}", error);
                if (!string.IsNullOrEmpty(errorDescription))
                {
                    writer.WriteLine("error_description: {0}", errorDescription);
                }
                if (!string.IsNullOrEmpty(errorUri))
                {
                    writer.WriteLine("error_uri: {0}", errorUri);
                }
                writer.Flush();
                body = Encoding.UTF8.GetString(memory.ToArray());
            }

            Response.ContentType = "text/plain;charset=UTF-8";
            Response.Headers["Content-Length"] = body.Length.ToString(CultureInfo.InvariantCulture);
            await Response.WriteAsync(body, Context.RequestAborted);
            // request is handled, does not pass on to application
            return true;
        }

        private Task<bool> SendErrorRedirectAsync(OAuthValidateClientRedirectUriContext clientContext, BaseValidatingContext<OAuthAuthorizationServerOptions> validatingContext)
        {
            if (clientContext == null)
            {
                throw new ArgumentNullException("clientContext");
            }

            string error = validatingContext.HasError ? validatingContext.Error : Constants.Errors.InvalidRequest;
            string errorDescription = validatingContext.HasError ? validatingContext.ErrorDescription : null;
            string errorUri = validatingContext.HasError ? validatingContext.ErrorUri : null;

            if (!clientContext.IsValidated)
            {
                // write error in response body if client_id or redirect_uri have not been validated
                return SendErrorPageAsync(error, errorDescription, errorUri);
            }

            // redirect with error if client_id and redirect_uri have been validated
            string location = QueryHelpers.AddQueryString(clientContext.RedirectUri, Constants.Parameters.Error, error);
            if (!string.IsNullOrEmpty(errorDescription))
            {
                location = QueryHelpers.AddQueryString(location, Constants.Parameters.ErrorDescription, errorDescription);
            }
            if (!string.IsNullOrEmpty(errorUri))
            {
                location = QueryHelpers.AddQueryString(location, Constants.Parameters.ErrorUri, errorUri);
            }
            Response.Redirect(location);
            // request is handled, does not pass on to application
            return Task.FromResult(true);
        }

        private static AuthenticationTicket ReturnOutcome(OAuthValidateTokenRequestContext validatingContext, BaseValidatingContext<OAuthAuthorizationServerOptions> grantContext, AuthenticationTicket ticket, string defaultError)
        {
            if (!validatingContext.IsValidated)
                return null;

            if (!grantContext.IsValidated)
            {
                if (grantContext.HasError)
                {
                    validatingContext.SetError(grantContext.Error, grantContext.ErrorDescription, grantContext.ErrorUri);
                }
                else
                {
                    validatingContext.SetError(defaultError);
                }

                return null;
            }

            if (ticket == null)
            {
                validatingContext.SetError(defaultError);
                return null;
            }

            return ticket;
        }

        private async Task<AuthenticationTicket> InvokeTokenEndpointAuthorizationCodeGrantAsync(OAuthValidateTokenRequestContext validatingContext, DateTimeOffset currentUtc)
        {
            TokenEndpointRequest tokenEndpointRequest = validatingContext.TokenRequest;

            var authorizationCodeContext = new AuthenticationTokenReceiveContext(Context, Options.AuthorizationCodeFormat, tokenEndpointRequest.AuthorizationCodeGrant.Code);

            await Options.AuthorizationCodeProvider.ReceiveAsync(authorizationCodeContext);

            AuthenticationTicket ticket = authorizationCodeContext.Ticket;

            if (ticket == null)
            {
                Logger.LogError("invalid authorization code");
                validatingContext.SetError(Constants.Errors.InvalidGrant);
                return null;
            }

            if (!ticket.Properties.ExpiresUtc.HasValue ||
                ticket.Properties.ExpiresUtc < currentUtc)
            {
                Logger.LogError("expired authorization code");
                validatingContext.SetError(Constants.Errors.InvalidGrant);
                return null;
            }

            string clientId;
            if (!ticket.Properties.Items.TryGetValue(Constants.Extra.ClientId, out clientId) ||
                !string.Equals(clientId, validatingContext.ClientContext.ClientId, StringComparison.Ordinal))
            {
                Logger.LogError("authorization code does not contain matching client_id");
                validatingContext.SetError(Constants.Errors.InvalidGrant);
                return null;
            }

            string redirectUri;
            if (ticket.Properties.Items.TryGetValue(Constants.Extra.RedirectUri, out redirectUri))
            {
                ticket.Properties.Items.Remove(Constants.Extra.RedirectUri);
                if (!string.Equals(redirectUri, tokenEndpointRequest.AuthorizationCodeGrant.RedirectUri, StringComparison.Ordinal))
                {
                    Logger.LogError("authorization code does not contain matching redirect_uri");
                    validatingContext.SetError(Constants.Errors.InvalidGrant);
                    return null;
                }
            }

            await Options.Provider.ValidateTokenRequest(validatingContext);

            var grantContext = new OAuthGrantAuthorizationCodeContext(
                Context, Options, ticket);

            if (validatingContext.IsValidated)
            {
                await Options.Provider.GrantAuthorizationCode(grantContext);
            }

            return ReturnOutcome(validatingContext, grantContext, grantContext.Ticket, Constants.Errors.InvalidGrant);
        }

        private async Task<AuthenticationTicket> InvokeTokenEndpointResourceOwnerPasswordCredentialsGrantAsync(OAuthValidateTokenRequestContext validatingContext, DateTimeOffset currentUtc)
        {
            TokenEndpointRequest tokenEndpointRequest = validatingContext.TokenRequest;

            await Options.Provider.ValidateTokenRequest(validatingContext);

            var grantContext = new OAuthGrantResourceOwnerCredentialsContext(
                                                                                Context,
                                                                                Options,
                                                                                validatingContext.ClientContext.ClientId,
                                                                                tokenEndpointRequest.ResourceOwnerPasswordCredentialsGrant.UserName,
                                                                                tokenEndpointRequest.ResourceOwnerPasswordCredentialsGrant.Password,
                                                                                tokenEndpointRequest.ResourceOwnerPasswordCredentialsGrant.Scope
                                                                            );

            if (validatingContext.IsValidated)
                await Options.Provider.GrantResourceOwnerCredentials(grantContext);

            return ReturnOutcome(validatingContext, grantContext, grantContext.Ticket, Constants.Errors.InvalidGrant);
        }

        private async Task<AuthenticationTicket> InvokeTokenEndpointClientCredentialsGrantAsync(OAuthValidateTokenRequestContext validatingContext, DateTimeOffset currentUtc)
        {
            TokenEndpointRequest tokenEndpointRequest = validatingContext.TokenRequest;

            await Options.Provider.ValidateTokenRequest(validatingContext);

            if (!validatingContext.IsValidated)
                return null;

            var grantContext = new OAuthGrantClientCredentialsContext(Context, Options, validatingContext.ClientContext.ClientId, tokenEndpointRequest.ClientCredentialsGrant.Scope);

            await Options.Provider.GrantClientCredentials(grantContext);

            return ReturnOutcome(validatingContext, grantContext, grantContext.Ticket, Constants.Errors.UnauthorizedClient);
        }

        private async Task<AuthenticationTicket> InvokeTokenEndpointRefreshTokenGrantAsync(OAuthValidateTokenRequestContext validatingContext, DateTimeOffset currentUtc)
        {
            TokenEndpointRequest tokenEndpointRequest = validatingContext.TokenRequest;

            var refreshTokenContext = new AuthenticationTokenReceiveContext(Context, Options.RefreshTokenFormat, tokenEndpointRequest.RefreshTokenGrant.RefreshToken);

            await Options.RefreshTokenProvider.ReceiveAsync(refreshTokenContext);

            AuthenticationTicket ticket = refreshTokenContext.Ticket;

            if (ticket == null)
            {
                Logger.LogError("invalid refresh token");
                validatingContext.SetError(Constants.Errors.InvalidGrant);
                return null;
            }

            if (!ticket.Properties.ExpiresUtc.HasValue || ticket.Properties.ExpiresUtc < currentUtc)
            {
                Logger.LogError("expired refresh token");
                validatingContext.SetError(Constants.Errors.InvalidGrant);
                return null;
            }

            await Options.Provider.ValidateTokenRequest(validatingContext);

            var grantContext = new OAuthGrantRefreshTokenContext(Context, Options, ticket, validatingContext.ClientContext.ClientId);

            if (validatingContext.IsValidated)
                await Options.Provider.GrantRefreshToken(grantContext);

            return ReturnOutcome(validatingContext, grantContext, grantContext.Ticket, Constants.Errors.InvalidGrant);
        }

        private async Task<AuthenticationTicket> InvokeTokenEndpointCustomGrantAsync(OAuthValidateTokenRequestContext validatingContext, DateTimeOffset currentUtc)
        {
            TokenEndpointRequest tokenEndpointRequest = validatingContext.TokenRequest;

            await Options.Provider.ValidateTokenRequest(validatingContext);

            var grantContext = new OAuthGrantCustomExtensionContext(Context, Options, validatingContext.ClientContext.ClientId, tokenEndpointRequest.GrantType, tokenEndpointRequest.CustomExtensionGrant.Parameters);

            if (validatingContext.IsValidated)
                await Options.Provider.GrantCustomExtension(grantContext);

            return ReturnOutcome(validatingContext, grantContext, grantContext.Ticket, Constants.Errors.UnsupportedGrantType);
        }

        private async Task<bool> InvokeAuthorizeEndpointAsync()
        {
            var authorizeRequest = new AuthorizeEndpointRequest(Request.Query);

            var clientContext = new OAuthValidateClientRedirectUriContext(Context, Options, authorizeRequest.ClientId, authorizeRequest.RedirectUri);

            if (!string.IsNullOrEmpty(authorizeRequest.RedirectUri))
            {
                bool acceptableUri = true;

                Uri validatingUri;

                if (!Uri.TryCreate(authorizeRequest.RedirectUri, UriKind.Absolute, out validatingUri))
                {
                    // The redirection endpoint URI MUST be an absolute URI
                    // http://tools.ietf.org/html/rfc6749#section-3.1.2
                    acceptableUri = false;
                }
                else if (!string.IsNullOrEmpty(validatingUri.Fragment))
                {
                    // The endpoint URI MUST NOT include a fragment component.
                    // http://tools.ietf.org/html/rfc6749#section-3.1.2
                    acceptableUri = false;
                }
                else if (!Options.AllowInsecureHttp && string.Equals(validatingUri.Scheme, "http", StringComparison.OrdinalIgnoreCase))
                {
                    // The redirection endpoint SHOULD require the use of TLS
                    // http://tools.ietf.org/html/rfc6749#section-3.1.2.1
                    acceptableUri = false;
                }
                if (!acceptableUri)
                {
                    clientContext.SetError(Constants.Errors.InvalidRequest);

                    return await SendErrorRedirectAsync(clientContext, clientContext);
                }
            }

            await Options.Provider.ValidateClientRedirectUri(clientContext);

            if (!clientContext.IsValidated)
            {
                Logger.LogVerbose("Unable to validate client information");

                return await SendErrorRedirectAsync(clientContext, clientContext);
            }

            var validatingContext = new OAuthValidateAuthorizeRequestContext(Context, Options, authorizeRequest, clientContext);

            if (string.IsNullOrEmpty(authorizeRequest.ResponseType))
            {
                Logger.LogVerbose("Authorize endpoint request missing required response_type parameter");

                validatingContext.SetError(Constants.Errors.InvalidRequest);
            }
            else if (!authorizeRequest.IsAuthorizationCodeGrantType && !authorizeRequest.IsImplicitGrantType)
            {
                Logger.LogVerbose("Authorize endpoint request contains unsupported response_type parameter");

                validatingContext.SetError(Constants.Errors.UnsupportedResponseType);
            }
            else
            {
                await Options.Provider.ValidateAuthorizeRequest(validatingContext);
            }

            if (!validatingContext.IsValidated)
            {
                // an invalid request is not processed further
                return await SendErrorRedirectAsync(clientContext, validatingContext);
            }

            _clientContext = clientContext;

            _authorizeEndpointRequest = authorizeRequest;

            var authorizeEndpointContext = new OAuthAuthorizeEndpointContext(Context, Options, authorizeRequest);

            await Options.Provider.AuthorizeEndpoint(authorizeEndpointContext);

            return authorizeEndpointContext.IsRequestCompleted;
        }

        private async Task InvokeTokenEndpointAsync()
        {
            DateTimeOffset currentUtc = Options.SystemClock.UtcNow;

            // remove milliseconds in case they don't round-trip
            currentUtc = currentUtc.Subtract(TimeSpan.FromMilliseconds(currentUtc.Millisecond));

            IFormCollection form = await Request.ReadFormAsync();

            var clientContext = new OAuthValidateClientAuthenticationContext(Context, Options, form);

            await Options.Provider.ValidateClientAuthentication(clientContext);

            if (!clientContext.IsValidated)
            {
                Logger.LogError("clientID is not valid.");

                if (!clientContext.HasError)
                    clientContext.SetError(Constants.Errors.InvalidClient);

                await SendErrorAsJsonAsync(clientContext);

                return;
            }

            var tokenEndpointRequest = new TokenEndpointRequest(form);

            var validatingContext = new OAuthValidateTokenRequestContext(Context, Options, tokenEndpointRequest, clientContext);

            AuthenticationTicket ticket = null;
            if (tokenEndpointRequest.IsAuthorizationCodeGrantType)
            {
                // Authorization Code Grant http://tools.ietf.org/html/rfc6749#section-4.1
                // Access Token Request http://tools.ietf.org/html/rfc6749#section-4.1.3
                ticket = await InvokeTokenEndpointAuthorizationCodeGrantAsync(validatingContext, currentUtc);
            }
            else if (tokenEndpointRequest.IsResourceOwnerPasswordCredentialsGrantType)
            {
                // Resource Owner Password Credentials Grant http://tools.ietf.org/html/rfc6749#section-4.3
                // Access Token Request http://tools.ietf.org/html/rfc6749#section-4.3.2
                ticket = await InvokeTokenEndpointResourceOwnerPasswordCredentialsGrantAsync(validatingContext, currentUtc);
            }
            else if (tokenEndpointRequest.IsClientCredentialsGrantType)
            {
                // Client Credentials Grant http://tools.ietf.org/html/rfc6749#section-4.4
                // Access Token Request http://tools.ietf.org/html/rfc6749#section-4.4.2
                ticket = await InvokeTokenEndpointClientCredentialsGrantAsync(validatingContext, currentUtc);
            }
            else if (tokenEndpointRequest.IsRefreshTokenGrantType)
            {
                // Refreshing an Access Token
                // http://tools.ietf.org/html/rfc6749#section-6
                ticket = await InvokeTokenEndpointRefreshTokenGrantAsync(validatingContext, currentUtc);
            }
            else if (tokenEndpointRequest.IsCustomExtensionGrantType)
            {
                // Defining New Authorization Grant Types
                // http://tools.ietf.org/html/rfc6749#section-8.3
                ticket = await InvokeTokenEndpointCustomGrantAsync(validatingContext, currentUtc);
            }
            else
            {
                // Error Response http://tools.ietf.org/html/rfc6749#section-5.2
                // The authorization grant type is not supported by the
                // authorization server.
                Logger.LogError("grant type is not recognized");

                validatingContext.SetError(Constants.Errors.UnsupportedGrantType);
            }

            if (ticket == null)
            {
                await SendErrorAsJsonAsync(validatingContext);
                return;
            }

            ticket.Properties.IssuedUtc = currentUtc;
            ticket.Properties.ExpiresUtc = currentUtc.Add(Options.AccessTokenExpireTimeSpan);

            var tokenEndpointContext = new OAuthTokenEndpointContext(Context, Options, ticket, tokenEndpointRequest);

            await Options.Provider.TokenEndpoint(tokenEndpointContext);

            if (tokenEndpointContext.TokenIssued)
            {
                ticket = new AuthenticationTicket(tokenEndpointContext.Principal, tokenEndpointContext.Properties, tokenEndpointContext.Options.AuthenticationScheme);
            }
            else
            {
                Logger.LogError("Token was not issued to tokenEndpointContext");
                validatingContext.SetError(Constants.Errors.InvalidGrant);
                await SendErrorAsJsonAsync(validatingContext);
                return;
            }

            var accessTokenContext = new AuthenticationTokenCreateContext(
                Context,
                Options.AccessTokenFormat,
                ticket);

            await Options.AccessTokenProvider.CreateAsync(accessTokenContext);

            string accessToken = accessTokenContext.Token;
            if (string.IsNullOrEmpty(accessToken))
            {
                accessToken = accessTokenContext.SerializeTicket();
            }

            DateTimeOffset? accessTokenExpiresUtc = ticket.Properties.ExpiresUtc;

            var refreshTokenCreateContext = new AuthenticationTokenCreateContext(Context, Options.RefreshTokenFormat, accessTokenContext.Ticket);

            await Options.RefreshTokenProvider.CreateAsync(refreshTokenCreateContext);

            string refreshToken = refreshTokenCreateContext.Token;

            var tokenEndpointResponseContext = new OAuthTokenEndpointResponseContext(Context, Options, ticket, tokenEndpointRequest, accessToken, tokenEndpointContext.AdditionalResponseParameters);

            await Options.Provider.TokenEndpointResponse(tokenEndpointResponseContext);

            MemoryStream stream, memoryStream = null;

            string body;

            try
            {
                stream = memoryStream = new MemoryStream();

                using (var writer = new JsonTextWriter(new StreamWriter(memoryStream)))
                {
                    memoryStream = null;

                    writer.WriteStartObject();
                    writer.WritePropertyName(Constants.Parameters.AccessToken);
                    writer.WriteValue(accessToken);
                    writer.WritePropertyName(Constants.Parameters.TokenType);
                    writer.WriteValue(Constants.TokenTypes.Bearer);

                    if (accessTokenExpiresUtc.HasValue)
                    {
                        TimeSpan? expiresTimeSpan = accessTokenExpiresUtc - currentUtc;
                        var expiresIn = (long)expiresTimeSpan.Value.TotalSeconds;
                        if (expiresIn > 0)
                        {
                            writer.WritePropertyName(Constants.Parameters.ExpiresIn);
                            writer.WriteValue(expiresIn);
                        }
                    }

                    if (!string.IsNullOrEmpty(refreshToken))
                    {
                        writer.WritePropertyName(Constants.Parameters.RefreshToken);
                        writer.WriteValue(refreshToken);
                    }

                    foreach (var additionalResponseParameter in tokenEndpointResponseContext.AdditionalResponseParameters)
                    {
                        writer.WritePropertyName(additionalResponseParameter.Key);
                        writer.WriteValue(additionalResponseParameter.Value);
                    }

                    writer.WriteEndObject();
                    writer.Flush();
                    body = Encoding.UTF8.GetString(stream.ToArray());

                    Response.ContentType = "application/json;charset=UTF-8";
                    Response.Headers["Cache-Control"] = "no-cache";
                    Response.Headers["Pragma"] = "no-cache";
                    Response.Headers["Expires"] = "-1";
                    Response.ContentLength = Encoding.UTF8.GetByteCount(body);
                }
            }
            finally
            {
                if (memoryStream != null)
                    memoryStream.Dispose();
            }

            await Response.WriteAsync(body, Encoding.UTF8, Context.RequestAborted);
        }

        private class Appender
        {
            private readonly char _delimiter;
            private readonly StringBuilder _sb;
            private bool _hasDelimiter;

            public Appender(string value, char delimiter)
            {
                _sb = new StringBuilder(value);
                _delimiter = delimiter;
                _hasDelimiter = value.IndexOf(delimiter) != -1;
            }

            public Appender Append(string name, string value)
            {
                _sb.Append(_hasDelimiter ? '&' : _delimiter)
                   .Append(Uri.EscapeDataString(name))
                   .Append('=')
                   .Append(Uri.EscapeDataString(value));

                _hasDelimiter = true;

                return this;
            }

            public override string ToString()
            {
                return _sb.ToString();
            }
        }


        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            return Task.FromResult<AuthenticateResult>(null);
        }

        #endregion

        #region Public Members       

        public override async Task<bool> HandleRequestAsync()
        {
            var matchRequestContext = new OAuthMatchContext(Context, Options);

            if (Options.AuthorizeEndpointPath.HasValue && Options.AuthorizeEndpointPath == Request.Path)
            {
                matchRequestContext.MatchesAuthorizeEndpoint();
            }
            else if (Options.TokenEndpointPath.HasValue && Options.TokenEndpointPath == Request.Path)
            {
                matchRequestContext.MatchesTokenEndpoint();
            }

            await Options.Provider.MatchEndpoint(matchRequestContext);

            if (matchRequestContext.HandledResponse)
                return true;

            if (matchRequestContext.Skipped)
                return false;

            if (matchRequestContext.IsAuthorizeEndpoint || matchRequestContext.IsTokenEndpoint)
            {
                if (!Options.AllowInsecureHttp && !Context.Request.IsHttps)
                {
                    Logger.LogWarning("Authorization server ignoring http request because AllowInsecureHttp is false.");

                    return false;
                }

                if (matchRequestContext.IsAuthorizeEndpoint)
                    return await InvokeAuthorizeEndpointAsync();

                if (matchRequestContext.IsTokenEndpoint)
                {
                    await InvokeTokenEndpointAsync();

                    return true;
                }
            }

            return false;
        }

        protected override async Task HandleSignInAsync(SignInContext context)
        {
            // only successful results of an authorize request are altered
            if (_clientContext == null || _authorizeEndpointRequest == null || Response.StatusCode != 200)
                return;

            if (context?.Principal == null)
                return;

            AuthenticationResponseGrant signin = new AuthenticationResponseGrant(context.Principal, new AuthenticationProperties(context.Properties));

            var returnParameter = new Dictionary<string, string>();

            if (_authorizeEndpointRequest.IsAuthorizationCodeGrantType)
            {
                DateTimeOffset currentUtc = Options.SystemClock.UtcNow;
                signin.Properties.IssuedUtc = currentUtc;
                signin.Properties.ExpiresUtc = currentUtc.Add(Options.AuthorizationCodeExpireTimeSpan);

                // associate client_id with all subsequent tickets
                signin.Properties.Items[Constants.Extra.ClientId] = _authorizeEndpointRequest.ClientId;
                if (!string.IsNullOrEmpty(_authorizeEndpointRequest.RedirectUri))
                {
                    // keep original request parameter for later comparison
                    signin.Properties.Items[Constants.Extra.RedirectUri] = _authorizeEndpointRequest.RedirectUri;
                }

                var tokenCreationContext = new AuthenticationTokenCreateContext(Context, Options.AuthorizationCodeFormat, new AuthenticationTicket(signin.Principal, signin.Properties, signin.Identity.AuthenticationType));

                await Options.AuthorizationCodeProvider.CreateAsync(tokenCreationContext);

                string code = tokenCreationContext.Token;
                if (string.IsNullOrEmpty(code))
                {
                    Logger.LogError("response_type code requires an Options.AuthorizationCodeProvider implementing a single-use token.");
                    var errorContext = new OAuthValidateAuthorizeRequestContext(Context, Options, _authorizeEndpointRequest, _clientContext);
                    errorContext.SetError(Constants.Errors.UnsupportedResponseType);
                    await SendErrorRedirectAsync(_clientContext, errorContext);
                    return;
                }

                var authResponseContext = new OAuthAuthorizationEndpointResponseContext(Context, Options, new AuthenticationTicket(signin.Principal, signin.Properties, signin.Identity.AuthenticationType), _authorizeEndpointRequest, null, code);

                await Options.Provider.AuthorizationEndpointResponse(authResponseContext);

                foreach (var parameter in authResponseContext.AdditionalResponseParameters)
                {
                    returnParameter[parameter.Key] = parameter.Value.ToString();
                }

                returnParameter[Constants.Parameters.Code] = code;

                if (!string.IsNullOrEmpty(_authorizeEndpointRequest.State))
                {
                    returnParameter[Constants.Parameters.State] = _authorizeEndpointRequest.State;
                }

                string location = string.Empty;
                if (_authorizeEndpointRequest.IsFormPostResponseMode)
                {
                    location = Options.FormPostEndpoint.ToString();
                    returnParameter[Constants.Parameters.RedirectUri] = _clientContext.RedirectUri;
                }
                else
                {
                    location = _clientContext.RedirectUri;
                }

                foreach (var key in returnParameter.Keys)
                {
                    location = QueryHelpers.AddQueryString(location, key, returnParameter[key]);
                }

                Response.Redirect(location);
            }
            else if (_authorizeEndpointRequest.IsImplicitGrantType)
            {
                string location = _clientContext.RedirectUri;

                DateTimeOffset currentUtc = Options.SystemClock.UtcNow;
                signin.Properties.IssuedUtc = currentUtc;
                signin.Properties.ExpiresUtc = currentUtc.Add(Options.AccessTokenExpireTimeSpan);

                // associate client_id with access token
                signin.Properties.Items[Constants.Extra.ClientId] = _authorizeEndpointRequest.ClientId;

                var accessTokenContext = new AuthenticationTokenCreateContext(Context, Options.AccessTokenFormat, new AuthenticationTicket(signin.Principal, signin.Properties, signin.Identity.AuthenticationType));

                await Options.AccessTokenProvider.CreateAsync(accessTokenContext);

                string accessToken = accessTokenContext.Token;
                if (string.IsNullOrEmpty(accessToken))
                {
                    accessToken = accessTokenContext.SerializeTicket();
                }

                DateTimeOffset? accessTokenExpiresUtc = accessTokenContext.Ticket.Properties.ExpiresUtc;

                var appender = new Appender(location, '#');

                appender.Append(Constants.Parameters.AccessToken, accessToken)
                        .Append(Constants.Parameters.TokenType, Constants.TokenTypes.Bearer);

                if (accessTokenExpiresUtc.HasValue)
                {
                    TimeSpan? expiresTimeSpan = accessTokenExpiresUtc - currentUtc;
                    var expiresIn = (long)(expiresTimeSpan.Value.TotalSeconds + .5);
                    appender.Append(Constants.Parameters.ExpiresIn, expiresIn.ToString(CultureInfo.InvariantCulture));
                }

                if (!string.IsNullOrEmpty(_authorizeEndpointRequest.State))
                {
                    appender.Append(Constants.Parameters.State, _authorizeEndpointRequest.State);
                }

                var authResponseContext = new OAuthAuthorizationEndpointResponseContext(Context, Options, new AuthenticationTicket(signin.Principal, signin.Properties, signin.Identity.AuthenticationType), _authorizeEndpointRequest, accessToken, null);

                await Options.Provider.AuthorizationEndpointResponse(authResponseContext);

                foreach (var parameter in authResponseContext.AdditionalResponseParameters)
                {
                    appender.Append(parameter.Key, parameter.Value.ToString());
                }

                Response.Redirect(appender.ToString());
            }
        }

        #endregion        
    }

}
