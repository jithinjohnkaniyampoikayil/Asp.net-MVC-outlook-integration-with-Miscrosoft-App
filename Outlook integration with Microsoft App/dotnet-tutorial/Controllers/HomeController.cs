﻿// Copyright (c) Microsoft. All rights reserved. Licensed under the MIT license. See full license at the bottom of this file.
using System;
using System.Configuration;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using System.Web;
using System.Web.Mvc;

using Microsoft.Identity.Client;
using Microsoft.Office365.OutlookServices;
using Microsoft.Owin.Security;
using Microsoft.Owin.Security.Cookies;
using Microsoft.Owin.Security.OpenIdConnect;

using dotnet_tutorial.TokenStorage;

namespace dotnet_tutorial.Controllers
{
    public class HomeController : Controller
    {
        public ActionResult Index()
        {
            if (Request.IsAuthenticated)
            {
                string userName = ClaimsPrincipal.Current.FindFirst("name").Value;
                string userId = ClaimsPrincipal.Current.FindFirst(ClaimTypes.NameIdentifier).Value;
                if (string.IsNullOrEmpty(userName) || string.IsNullOrEmpty(userId))
                {
                    // Invalid principal, sign out
                    return RedirectToAction("SignOut");
                }

                // Since we cache tokens in the session, if the server restarts
                // but the browser still has a cached cookie, we may be
                // authenticated but not have a valid token cache. Check for this
                // and force signout.
                SessionTokenCache tokenCache = new SessionTokenCache(userId, HttpContext);
                if (tokenCache.Count <= 0)
                {
                    // Cache is empty, sign out
                    return RedirectToAction("SignOut");
                }

                ViewBag.UserName = userName;
            }
            return View();
        }

        public void SignIn()
        {
            if (!Request.IsAuthenticated)
            {
                // Signal OWIN to send an authorization request to Azure
                HttpContext.GetOwinContext().Authentication.Challenge(
                  new AuthenticationProperties { RedirectUri = "/" },
                  OpenIdConnectAuthenticationDefaults.AuthenticationType);
            }
        }

        public void SignOut()
        {
            if (Request.IsAuthenticated)
            {
                string userId = ClaimsPrincipal.Current.FindFirst(ClaimTypes.NameIdentifier).Value;

                if (!string.IsNullOrEmpty(userId))
                {
                    string appId = ConfigurationManager.AppSettings["ida:AppId"];
                    // Get the user's token cache and clear it
                    SessionTokenCache tokenCache = new SessionTokenCache(userId, HttpContext);
                    tokenCache.Clear(appId);
                }
            }
            // Send an OpenID Connect sign-out request. 
            HttpContext.GetOwinContext().Authentication.SignOut(
              CookieAuthenticationDefaults.AuthenticationType);
            Response.Redirect("/");
        }

        public async Task<string> GetAccessToken()
        {
            string accessToken = null;

            // Load the app config from web.config
            string appId = ConfigurationManager.AppSettings["ida:AppId"];
            string appPassword = ConfigurationManager.AppSettings["ida:AppPassword"];
            string redirectUri = ConfigurationManager.AppSettings["ida:RedirectUri"];
            string[] scopes = ConfigurationManager.AppSettings["ida:AppScopes"]
                .Replace(' ', ',').Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries);

            // Get the current user's ID
            string userId = ClaimsPrincipal.Current.FindFirst(ClaimTypes.NameIdentifier).Value;

            if (!string.IsNullOrEmpty(userId))
            {
                // Get the user's token cache
                SessionTokenCache tokenCache = new SessionTokenCache(userId, HttpContext);

                ConfidentialClientApplication cca = new ConfidentialClientApplication(
                    appId, redirectUri, new ClientCredential(appPassword), tokenCache);

                // Call AcquireTokenSilentAsync, which will return the cached
                // access token if it has not expired. If it has expired, it will
                // handle using the refresh token to get a new one.
                AuthenticationResult result = await cca.AcquireTokenSilentAsync(scopes);

                accessToken = result.Token;
            }

            return accessToken;
        }

        public async Task<string> GetUserEmail()
        {
            OutlookServicesClient client =
                new OutlookServicesClient(new Uri("https://outlook.office.com/api/v2.0"), GetAccessToken);

            try
            {
                var userDetail = await client.Me.ExecuteAsync();
                return userDetail.EmailAddress;
            }
            catch (MsalException ex)
            {
                return string.Format("#ERROR#: Could not get user's email address. {0}", ex.Message);
            }
        }

        public async Task<ActionResult> Inbox()
        {
            string token = await GetAccessToken();
            if (string.IsNullOrEmpty(token))
            {
                // If there's no token in the session, redirect to Home
                return Redirect("/");
            }

            string userEmail = await GetUserEmail();

            OutlookServicesClient client =
                new OutlookServicesClient(new Uri("https://outlook.office.com/api/v2.0"), GetAccessToken);

            //client.Context.SendingRequest2 += new EventHandler<Microsoft.OData.Client.SendingRequest2EventArgs>(
            //    (sender, e) => InsertXAnchorMailboxHeader(sender, e, userEmail));

            try
            {
                var mailResults = await client.Me.Messages
                                    .OrderByDescending(m => m.ReceivedDateTime)
                                    .Take(10)
                                    .ExecuteAsync();

                var displayResults = mailResults.CurrentPage.Select(m => new Models.DisplayMessage(m.Subject, m.ReceivedDateTime, m.From));

                return View(displayResults);
            }
            catch (MsalException ex)
            {
                return RedirectToAction("Error", "Home", new { message = "ERROR retrieving messages", debug = ex.Message }); 
            }
        }

        public async Task<ActionResult> Calendar()
        {
            string token = await GetAccessToken();
            if (string.IsNullOrEmpty(token))
            {
                // If there's no token in the session, redirect to Home
                return Redirect("/");
            }

            string userEmail = await GetUserEmail();

            OutlookServicesClient client =
                new OutlookServicesClient(new Uri("https://outlook.office.com/api/v2.0"), GetAccessToken);

            client.Context.SendingRequest2 += new EventHandler<Microsoft.OData.Client.SendingRequest2EventArgs>(
                (sender, e) => InsertXAnchorMailboxHeader(sender, e, userEmail));

            try
            {
                var eventResults = await client.Me.Events
                                    .OrderByDescending(e => e.Start.DateTime)
                                    .Take(10)
                                    .Select(e => new Models.DisplayEvent(e.Subject, e.Start.DateTime, e.End.DateTime))
                                    .ExecuteAsync();

                return View(eventResults.CurrentPage);
            }
            catch (MsalException ex)
            {
                return RedirectToAction("Error", "Home", new { message = "ERROR retrieving events", debug = ex.Message });
            }
        }

        public async Task<ActionResult> Contacts()
        {
            string token = await GetAccessToken();
            if (string.IsNullOrEmpty(token))
            {
                // If there's no token in the session, redirect to Home
                return Redirect("/");
            }

            string userEmail = await GetUserEmail();

            OutlookServicesClient client =
                new OutlookServicesClient(new Uri("https://outlook.office.com/api/v2.0"), GetAccessToken);

            client.Context.SendingRequest2 += new EventHandler<Microsoft.OData.Client.SendingRequest2EventArgs>(
                (sender, e) => InsertXAnchorMailboxHeader(sender, e, userEmail));

            try
            {
                var contactResults = await client.Me.Contacts
                                    .OrderBy(c => c.DisplayName)
                                    .Take(10)
                                    .Select(c => new Models.DisplayContact(c.DisplayName, c.EmailAddresses, c.MobilePhone1))
                                    .ExecuteAsync();

                return View(contactResults.CurrentPage);
            }
            catch (MsalException ex)
            {
                return RedirectToAction("Error", "Home", new { message = "ERROR retrieving contacts", debug = ex.Message });
            }
        }

        private void InsertXAnchorMailboxHeader(object sender, Microsoft.OData.Client.SendingRequest2EventArgs e, string email)
        {
            e.RequestMessage.SetHeader("X-AnchorMailbox", email);
        }

        public ActionResult About()
        {
            ViewBag.Message = "Your application description page.";

            return View();
        }

        public ActionResult Contact()
        {
            ViewBag.Message = "Your contact page.";

            return View();
        }

        public ActionResult Error(string message, string debug)
        {
            ViewBag.Message = message;
            ViewBag.Debug = debug;
            return View("Error");
        }
    }
}

// MIT License:

// Permission is hereby granted, free of charge, to any person obtaining
// a copy of this software and associated documentation files (the
// ""Software""), to deal in the Software without restriction, including
// without limitation the rights to use, copy, modify, merge, publish,
// distribute, sublicense, and/or sell copies of the Software, and to
// permit persons to whom the Software is furnished to do so, subject to
// the following conditions:

// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.

// THE SOFTWARE IS PROVIDED ""AS IS"", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
// LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
// OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
// WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.