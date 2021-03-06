﻿using Constants;
using Microsoft.AspNet.Authentication.Cookies;
using Microsoft.AspNet.Builder;
using Microsoft.AspNet.Http;
using Microsoft.Extensions.DependencyInjection;
using OAuth.AspNet.AuthServer;
using System;

namespace AuthorizationServer
{

    public partial class Startup
    {        
        public void ConfigureServices(IServiceCollection services)
        {
            services.AddMvc();
        }

        public void Configure(IApplicationBuilder app)
        {
            app.UseIISPlatformHandler();

            // Enable Application Sign In Cookie
            app.UseCookieAuthentication(
                                           options => {
                                                          options.AuthenticationScheme = "Application";
                                                          options.AutomaticAuthenticate = false;
                                                          options.LoginPath = new PathString(Paths.LoginPath);
                                                          options.LogoutPath = new PathString(Paths.LogoutPath);
                                                      }
                                       );

            app.UseCookieAuthentication(
                                           options => {
                                                          options.AuthenticationScheme = "External";
                                                          options.AutomaticAuthenticate = false;
                                                          options.CookieName = CookieAuthenticationDefaults.CookiePrefix + options.AuthenticationScheme;
                                                          options.ExpireTimeSpan = TimeSpan.FromMinutes(5);
                                                      }
                                       );

            // Enable google authentication
            app.UseGoogleAuthentication(
                                           options => {
                                                          options.ClientId = "309638599461-a673etlplktupuk18bo12bfljgfo4tad.apps.googleusercontent.com";
                                                          options.ClientSecret = "YQYDFkClqu6wiSCKukaQqdfW";
                                                          options.SignInScheme = "External";
                                                          options.AutomaticAuthenticate = false;
                                                      }
                                       );

            // Setup Authorization Server
            app.UseOAuthAuthorizationServer(
                                               options => { 
                                                              options.AuthorizeEndpointPath = new PathString(Paths.AuthorizePath);
                                                              options.TokenEndpointPath = new PathString(Paths.TokenPath);
                                                              options.ApplicationCanDisplayErrors = true;

                                                             #if DEBUG
                                                              options.AllowInsecureHttp = true;
                                                             #endif

                                                              options.Provider = new OAuthAuthorizationServerProvider
                                                                                 {
                                                                                     OnValidateClientRedirectUri = ValidateClientRedirectUri,
                                                                                     OnValidateClientAuthentication = ValidateClientAuthentication,
                                                                                     OnGrantResourceOwnerCredentials = GrantResourceOwnerCredentials,
                                                                                     OnGrantClientCredentials = GrantClientCredetails
                                                                                 };

                                                              options.AuthorizationCodeProvider = new AuthenticationTokenProvider
                                                                                                  {
                                                                                                      OnCreate = CreateAuthenticationCode,
                                                                                                      OnReceive = ReceiveAuthenticationCode,
                                                                                                  };

                                                              options.RefreshTokenProvider = new AuthenticationTokenProvider
                                                                                             {
                                                                                                 OnCreate = CreateRefreshToken,
                                                                                                 OnReceive = ReceiveRefreshToken,
                                                                                             };

                                                              options.AutomaticAuthenticate = false;
                                                          }
                                           );         

            app.UseMvc(
                          routes =>
                          {
                              routes.MapRoute(
                                                 "DefaultMvc",
                                                 "{controller}/{action}/{id?}",
                                                 new { action = "Index" }
                                             );

                          }
                      );
        }
    }

}
