using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Diagnostics;
using HtmlAgilityPack;
using System.ComponentModel;
// using System.Windows.Media;

namespace mac {
    public enum LoginResult { OK, Fail, Cancel };

    /// <summary>
    /// Checks whether we have internet access.  If web requests are redirected to a login
    /// page then try to fill out the login page to get full internet access
    /// </summary>
    public class InternetAccess : INotifyPropertyChanged {
        // should be a site with checkable output as some providers (Tomizone) ninja-redirect
        // might as well stick with msftncsi.com as this is what windows uses anyway after getting a new ip
        private const string RedirectTestLookup = "www.msftncsi.com";
        private const string RedirectTestUrl = "http://www.msftncsi.com/ncsi.txt";

        private string _debugFilename;
        private string _progress;
        private int _progressMaxLines;

        // TODO provider stuff (and login macros) should be off in lua or some sort of config instead of coded
        private string _providerName;
        private int _providerAutoMinutes;
        private int _providerAutoMegabytes;

        public event PropertyChangedEventHandler PropertyChanged;

        public enum State { Offline, Online, Limited };
        public enum Provider { NzAklLibrary, NzAklTomizone, Unknown };

        private HttpStatusCode _lastResponseStatus;
        private string _lastResponseContent, _lastResponseLocation, _lastUrl;
        private readonly CookieContainer _cookieContainer = new CookieContainer();

        public InternetAccess() {
            _lastResponseStatus = 0;
            _lastResponseContent = null;
            _lastResponseLocation = null;
            _lastUrl = null;

            _progress = ""; 
            _progressMaxLines = 10;

            _providerAutoMinutes = 0;
            _providerAutoMegabytes = 0;
            _providerName = "";
        }

        // FIXME logging
        void _Debug(string message) {
            // Debug.WriteLine(message);

            if (string.IsNullOrEmpty(_debugFilename)) 
                return;
            System.IO.File.AppendAllText(@_debugFilename, string.Format("--- %s%s%s%s", 
                DateTime.Now.ToLongTimeString(), Environment.NewLine, message, Environment.NewLine));
        }

        public string DebugFilename { get { return _debugFilename; } set { _debugFilename = value; _Debug("[InternetAccess] debug toggled"); } }
        public int ProgressMaxLines { get { return _progressMaxLines; } set { _progressMaxLines = value; } }
        public string ProviderName { get { return _providerName; } set { _providerName = value; } }
        public int ProviderAutoMinutes { get { return _providerAutoMinutes; } set { _providerAutoMinutes = value; } }
        public int ProviderAutoMegabytes { get { return _providerAutoMegabytes; } set { _providerAutoMegabytes = value; } }

        public string Progress { 
            get { 
                return _progress; 
            } 
            set { 
                _progress = value;

                if (PropertyChanged != null)
                    PropertyChanged(this, new PropertyChangedEventArgs("Progress"));
            } 
        }

        /*
        public void DumpProgress(string filename) {
            Debug.WriteLine(string.Format("Dumping _progress to {0}", filename));
            System.IO.File.WriteAllText(@filename, string.Format("Progress is:\n----------\n{0}\n----------\n", _progress));
        }
        */

        public void AddProgress(string message) {
            if (string.IsNullOrEmpty(message))
                _progress = "";
            else {
                _progress = string.Format("{0}[{1}] {2}{3}", _progress, DateTime.Now.ToString(@"HH\:mm\:ss"), 
                    message, Environment.NewLine);

                while (_progress.Split(Environment.NewLine.ToCharArray()[0]).Count() > _progressMaxLines)
                    _progress = _progress.Substring(_progress.IndexOf(Environment.NewLine, StringComparison.Ordinal) + 
                        Environment.NewLine.Length);
            }

            if (PropertyChanged != null)
                PropertyChanged(this, new PropertyChangedEventArgs("Progress"));
        }

        public void AddProgress(string message, int indent) {
            string s = "";
            for (int i = Math.Max(indent, 1); i <= Math.Min(indent, 3); i++)
                s += "\t";

            AddProgress(s + message);
        }

        /// <summary>
        /// Wrapper for submitting an HTTP GET request
        /// </summary>
        /// <param name="url"></param>
        /// <returns>False on exception, true otherwise.  Saves page content, etc to _last*</returns>
        private bool Get(string url) {
            _lastResponseStatus = 0;
            _lastResponseContent = null;
            _lastResponseLocation = null;
            _lastUrl = null;

            try {
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.AllowAutoRedirect = false;
                req.CookieContainer = _cookieContainer;

                using (var res = (HttpWebResponse)req.GetResponse()) {
                    _lastResponseStatus = res.StatusCode;
                    _lastResponseLocation = res.GetResponseHeader("Location");
                    _lastUrl = url;

                    using (var sr = new StreamReader(res.GetResponseStream()))
                        _lastResponseContent = sr.ReadToEnd();

                    _Debug(string.Format("url='{0}',res.Location='{1}',content={2}{3}", url, _lastResponseLocation,
                        Environment.NewLine, _lastResponseContent));
                }
            } 
            catch (Exception e) {
                _lastResponseStatus = HttpStatusCode.InternalServerError; // close enough
                _lastResponseContent = e.Message;

                return false;
            }

            return true;
        }

        /// <summary>
        /// Wrapper for submitting an HTTP POST request
        /// </summary>
        /// <param name="url"></param>
        /// <param name="param">POST parameters</param>
        /// <returns>False on exception, true otherwise.  Saves page content, etc to _last*</returns>
        private bool Post(string url, IEnumerable<KeyValuePair<string, string>> param) {
            _lastResponseStatus = 0;
            _lastResponseContent = null;
            _lastResponseLocation = null;
            _lastUrl = null;

            try {
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.AllowAutoRedirect = false;
                req.ContentType = "application/x-www-form-urlencoded";
                req.Method = "POST";
                req.CookieContainer = _cookieContainer;

                string postData = "";
                foreach (KeyValuePair<string, string> p in param) {
                    if (postData.Length != 0)
                        postData += "&";
                    postData += p.Key + "=" + p.Value;
                }

                byte[] bytes = Encoding.UTF8.GetBytes(postData);
                req.ContentLength = bytes.Length;

                Stream reqStream = req.GetRequestStream();
                reqStream.Write(bytes, 0, bytes.Length);
                reqStream.Dispose();

                using (var res = (HttpWebResponse)req.GetResponse()) {
                    _lastResponseStatus = res.StatusCode;
                    _lastResponseLocation = res.GetResponseHeader("Location");
                    _lastUrl = url;

                    using (var sr = new StreamReader(res.GetResponseStream()))
                        _lastResponseContent = sr.ReadToEnd();

                    _Debug(string.Format("url='{0}',res.Location='{1}',content={2}{3}", url, _lastResponseLocation,
                        Environment.NewLine, _lastResponseContent));
                }
            }
            catch (Exception e) {
                _lastResponseStatus = HttpStatusCode.InternalServerError; // close enough
                _lastResponseContent = e.Message;

                return false;
            }

            return true;
        }

        /// <summary>
        /// Attempt to detect/return current state of internet connectivity
        /// </summary>
        /// <returns>State (State.Online, State.Offline, State.Limited)</returns>
        public State GetState() {
            // attempt to resolve RedirectTestLookup
            // fail: offline
            // success: online or limited
            AddProgress("lookup " + RedirectTestLookup, 1);
            try {
                var iph = Dns.GetHostEntry(RedirectTestLookup);
                if (iph.AddressList.Length == 0)
                    return State.Offline;
            } 
            catch (Exception e) {
                AddProgress(string.Format("lookup failed ({0})", e.Message.ToLower()), 2);
                return State.Offline;
            }

            // attempt to fetch test url
            // fail: offline
            // success + expected content (Microsoft NCSI): online
            // -- success + no redirect: online
            // success: limited
            AddProgress(string.Format("fetch {0}", RedirectTestUrl), 1);
            
            if (! Get(RedirectTestUrl)) 
                return State.Offline;
            if (_lastResponseStatus == HttpStatusCode.OK && _lastResponseContent != null && _lastResponseContent.Equals("Microsoft NCSI"))
                return State.Online;
            
            return State.Limited;

            // attempt to resolve RedirectTestLookup
            // fail: offline
            // success + expected content (131.107.255.255): limited
            // success: limited(?)
        }

        /// <summary>
        /// Attempt to determine which network provider we're currently connected to
        /// </summary>
        /// <param name="html">html to parse to look for clues</param>
        /// <returns>Provider (Provider.NZ_AKL_Library, etc)</returns>
        public Provider GetProvider(string html) {
            AddProgress("lookup internet provider", 1);

            _providerName = "Unknown";
            _providerAutoMegabytes = 0;
            _providerAutoMinutes = 0;

            if (string.IsNullOrEmpty(html))
                return Provider.Unknown;

            var htmlDoc = new HtmlDocument();

            htmlDoc.LoadHtml(html);
            if (htmlDoc.DocumentNode == null) {
                _Debug("[InternetAccess.GetProvider] failed to parse html");
                return Provider.Unknown;
            }

            // <title>
            var title = htmlDoc.DocumentNode.SelectSingleNode("//title");
            if (title != null) {
                if (title.InnerText.Equals("Auckland City Library WIFI Login"))
                    return Provider.NzAklLibrary;
            }

            // <meta http-equiv="refresh" content="0; url=loginpage" />
            var metaRefresh = htmlDoc.DocumentNode.SelectSingleNode("//meta[@http-equiv='refresh']");
            if (metaRefresh != null) {
                var url = _value(metaRefresh.Attributes["content"]);

                if (url.Length > 7 && url.Substring(0, 7).ToUpper().Equals("0; URL=")) {
                    url = url.Substring(7);
                    // follow the url so page content is in _lastResponseContent for the next step
                    Get(url);

                    if (url.Substring(0, 27).Equals("http://hotspot.tomizone.com")) {
                        return Provider.NzAklTomizone;
                    }
                }
                else {
                    _Debug("[InternetAccess.GetProvider] Unexpected meta-equiv=refresh prefix: " + url);
                }

            }

            return Provider.Unknown;
        }

        private string _value(HtmlAttribute x) {
            if (x == null)
                return "";

            return x.Value;
        }

        /// <summary>
        /// POST back to the form in the provided html
        /// </summary>
        /// <param name="url">URL to submit to, uses form action= if not provided</param>
        /// <param name="html">HTML to parse for form contents</param>
        /// <param name="formId">Id of the form to use, uses first form on the page if not provided</param>
        /// <returns></returns>
        private bool FormRepost(string url, string html, string formId) {
            var htmlDoc = new HtmlDocument();
            htmlDoc.LoadHtml(html);

            if (htmlDoc.DocumentNode == null) {
                _Debug("[FormRepost] html parse failed");
                return false;
            }

            HtmlNode form;
            if (string.IsNullOrEmpty(formId))
                form = htmlDoc.DocumentNode.SelectSingleNode("//form"); // get the first form if no id specified
            else
                form = htmlDoc.DocumentNode.SelectSingleNode("//form[@id='" + formId + "']");
            if (form == null) {
                _Debug("[FormRepost] failed to find form in parsed html");
                return false;
            }

            var param = new List<KeyValuePair<string, string>>();
            foreach (var input in form.SelectNodes("//input")) {
                var name = _value(input.Attributes["name"]);
                var value = _value(input.Attributes["value"]);

                param.Add(new KeyValuePair<string, string>(name, value));
            }

            // no url + relative action should be an error?
            if (url == null)
                url = _value(form.Attributes["action"]);
            else
                url = url + _value(form.Attributes["action"]);

            if (! Post(url, param)) {
                _Debug("[FormRepost] post failed");
                return false;
            }

            return true;
        }

        /// <summary>
        /// Login handler for Auckland Library Free Wifi
        /// </summary>
        /// <param name="url"></param>
        /// <returns>true on logon success, false on failure (logon failed, expected content missing)</returns>
        private LoginResult LoginNzAklLibrary(string url) {
            _providerName = "Auckland Public Library";
            _providerAutoMegabytes = 100;
            _providerAutoMinutes = 0;

            // get the url we've been redirected to
            var prefix = url.Substring(0, url.IndexOf("/", StringComparison.Ordinal));
            url = url.Substring(url.IndexOf("/", StringComparison.Ordinal) + 2);
            var host = url.Substring(0, url.IndexOf("/", StringComparison.Ordinal));

            url = prefix + "//" + host;

            // look for wlform and post back to it
            AddProgress("posting to 'wlform'", 2);
            if (! FormRepost(url, _lastResponseContent, "wlform")) {
                _Debug("[InternetAccess.Login_NZAklLibrary] wlform failed");
                // look for 'Sorry, you have exceeded'
                if (_lastResponseContent.IndexOf("Sorry, you have exceeded") != -1) {
                    AddProgress("You have exceeded download limit - restart interface for a new MAC");
                    return LoginResult.Cancel;
                }
                return LoginResult.Fail;
            }

            // look for prepro and post back to it..
            AddProgress("posting to 'prepro'", 2);
            if (! FormRepost(null, _lastResponseContent, "prepro")) {
                _Debug("[InternetAccess.Login_NZAklLibrary] prepro failed");
                return LoginResult.Fail;
            }

            // should be a redirect to a transport page
            AddProgress("redirecting", 2);
            if (_lastResponseLocation == null || ! Get(_lastResponseLocation)) {
                _Debug("[InternetAccess.Login_NZAklLibrary] missing or failed redirect");
                return LoginResult.Fail;
            }

            // should be online
            return GetState() == State.Online ? LoginResult.OK : LoginResult.Fail;
        }

        /// <summary>
        /// Login handler for Auckland Tomizone Free Wifi
        /// </summary>
        /// <returns>true on logon success, false on failure (logon failed, expected content missing)</returns>
        private LoginResult LoginNzAklTomizone() {
            _providerName = "Auckland Tomizone";
            _providerAutoMegabytes = 30;
            _providerAutoMinutes = 30;

            // look for first form and post back to it
            AddProgress("posting to 'form1'", 2);
            if (! FormRepost(null, _lastResponseContent, null)) {
                _Debug("[InternetAccess.Login_NZAklTomizone] form1 failed");
                return LoginResult.Fail;
            }

            // should be a redirect
            AddProgress("redirecting", 2);
            if (_lastResponseLocation == null || !Get(_lastResponseLocation)) {
                _Debug("InternetAccess.Login_NZAklTomizone] redirect missing or failed");
                return LoginResult.Fail;
            }

            // and another redirect
            AddProgress("redirecting", 2);
            if (_lastResponseLocation == null || !Get(_lastResponseLocation)) {
                _Debug("InternetAccess.Login_NZAklTomizone] redirect missing or failed");
                return LoginResult.Fail;
            }

            // and another redirect
            AddProgress("tomizone love their fucking redirects eh", 2);
            if (_lastResponseLocation == null || !Get(_lastResponseLocation)) {
                _Debug("[InternetAccess.Login_NZAklTomizone] redirect missing or failed");
                return LoginResult.Fail;
            }

            // login page has 5 or 6 forms all with id/name "login_form", just submit to the free interwebs one
            AddProgress("posting to 'login_form'", 2);
            var param = new List<KeyValuePair<string, string>> {new KeyValuePair<string, string>("login", "Accept Terms and Conditions")};
            if (! Post("https://secure.tomizone.com/portal/free-login", param)) {
                _Debug("InternetAccess.Login_NZAklTomizone] free-login form missing or failed");
                return LoginResult.Fail;
            }

            // one last redirect
            // http://hotspot.tomizone.com/login?username=*3FX3QY67XR9&password=null&popup=false&dst=https://secure.tomizone.com/portal/success
            AddProgress("redirecting", 2);
            if (_lastResponseLocation == null || !Get(_lastResponseLocation)) {
                _Debug("[InternetAccess.Login_NZAklTomizone] redirect missing or failed");
                return LoginResult.Fail;
            }

            // should be online
            return GetState() == State.Online ? LoginResult.OK : LoginResult.Fail;
        }

        /// <summary>
        /// Default handler, follows any redirects and submits the first form on any page
        /// Does a final online recheck when there are no more redirects or forms
        /// </summary>
        /// <returns>true on logon success, false on failure (logon failed, expected content missing)</returns>
        private LoginResult LoginDefault() {
            _providerName = "Default";
            _providerAutoMegabytes = 0;
            _providerAutoMinutes = 0;

            for (int i = 0; i < 10; i++) { // don't keep trying forever
                // any redirect to follow?
                if (_lastResponseLocation != null && Get(_lastResponseLocation)) {
                    Debug.WriteLine("[InternetAccess.Login_Unknown] location redirect");
                }
                // how about a form?
                else if (FormRepost(null, _lastResponseContent, null)) {
                    Debug.WriteLine("[InternetAccess.Login_Unknown] submitted first form");
                }
                // no redirect, no form, break out and see if we're online
                else {
                    break;
                }
            }

            // check online
            return GetState() == State.Online ? LoginResult.OK : LoginResult.Fail;
        }

        /// <summary>
        /// Login handler wrapper. Determines provider from redirected page content and goes off to the
        /// handler for that provider
        /// </summary>
        /// <returns>true on logon success, false on failure</returns>
        public LoginResult Login() {
            var url = RedirectTestUrl;

            if (! _lastUrl.Equals(url)) // can use the cached result if the last get/post was to the ncsi page
                if (! Get(url))
                    return LoginResult.Fail;

            if (_lastResponseStatus == HttpStatusCode.OK && _lastResponseContent != null && _lastResponseContent.Equals("Microsoft NCSI"))
                return LoginResult.OK;

            if (_lastResponseStatus == HttpStatusCode.Redirect && _lastResponseLocation != null) {
                // follow the redirect then keep processing as if the redirected page were the one we were already on
                url = _lastResponseLocation;
                Get(url);
            }

            // if we're this far - we're not online but the web fetch didn't outright fail
            // so _lastResponseContent should have content to determine which network we're on
            var p = GetProvider(_lastResponseContent);
            // _Debug("[InternetAccess.Login] provider '" + p + "' detected");
            AddProgress("provider is '" + p + "'", 1);

            switch (p) {
                case Provider.NzAklLibrary:
                    return LoginNzAklLibrary(url);
                case Provider.NzAklTomizone:
                    return LoginNzAklTomizone();
                case Provider.Unknown:
                    return LoginDefault();
            }

            AddProgress(string.Format("no handler for '{0}'", p), 1);
            return LoginResult.Cancel;
        }
    }
}
