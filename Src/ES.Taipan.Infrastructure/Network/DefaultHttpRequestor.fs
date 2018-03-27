﻿namespace ES.Taipan.Infrastructure.Network

open System
open System.Collections.Generic
open System.Reflection
open System.Text
open System.Security.Cryptography.X509Certificates
open System.IO
open System.Net
open System.Net.Security
open Microsoft.FSharp.Control.WebExtensions
open ES.Taipan.Infrastructure.Threading
open ES.Taipan.Infrastructure.Text
open ES.Fslog

type DefaultHttpRequestor(defaultSettings: HttpRequestorSettings, requestNotificationCallback: HttpRequest * Boolean -> unit, logProvider: ILogProvider) =
    let _requestGateTimeout = 1000 * 60 * 60 * 10 // 10 minutes
    let _maxParallelism = 100
    let _requestGate = new RequestGate(_maxParallelism)
    let _certificationValidate = new Event<CertificationValidateEventArgs>()
    let _logger = new HttpRequestorLogger()
    let _seleniumDriverSyncRoot = new Object()
    let mutable _httpAuthenticationToken: String option = None
    let mutable _httpDigestInfo: DigestAuthenticationInfo option = None
    let mutable _skipAuthenticationProcess = false
    let mutable _seleniumDriver: SeleniumDriver option = None

    // do this trick to have a depth copy of the settings in order to be modified in a safe way for each instance
    let _settings = new HttpRequestorSettings()
    do _settings.AcquireSettingsFromXml(defaultSettings.ToXml())

    let doCertificateValidation (sender: Object) (certificate: X509Certificate) (chain: X509Chain) (policy:SslPolicyErrors) =
        _certificationValidate.Trigger(new CertificationValidateEventArgs(certificate, chain, policy))
        true

    let applyFixToUnescapedCharacters(httpRequest: HttpRequest) =
        if httpRequest.Uri.Query.Contains("#") then
            let uriBuilder = new UriBuilder(httpRequest.Uri)
            uriBuilder.Query <- uriBuilder.Query.Replace("#", "%23")
            httpRequest.Uri <- uriBuilder.Uri        

    let applySettingsToRequest(httpRequest: HttpRequest) =   
        // add http header
        for kv in _settings.AdditionalHttpHeaders do
            let headerName = kv.Key
            let headerValue = kv.Value
            let headerAlredyPresent =
                httpRequest.Headers |> Seq.toList
                |> List.exists (fun hdr -> hdr.Name.Equals(headerName, StringComparison.Ordinal))
            if not headerAlredyPresent then
                httpRequest.Headers.Add(new HttpHeader(Name=headerName, Value=headerValue))

        // add cookies
        for kv in _settings.AdditionalCookies do
            let cookieName = kv.Key
            let cookieValue = kv.Value
            let cookieAlredyPresent =
                httpRequest.Cookies |> Seq.toList
                |> List.exists (fun ck -> ck.Name.Equals(cookieName, StringComparison.Ordinal))
            if not cookieAlredyPresent then
                httpRequest.Cookies.Add(new Cookie(cookieName, cookieValue, "/", httpRequest.Uri.Host))
                    
    let applyAuthenticationInfoToRequestIfNeeded(httpRequest: HttpRequest) =        
        match _settings.Authentication.Type with
        | HttpBasic -> 
            // can pre-create the auth header
            match _httpAuthenticationToken with
            | None ->
                let token = String.Format("{0}:{1}", _settings.Authentication.Username, _settings.Authentication.Password)
                _httpAuthenticationToken <- Some <| String.Format("Basic {0}", toAsciiBase64(token))
            | _ -> ()
            httpRequest.Headers.Add(new HttpHeader(Name="Authorization", Value=_httpAuthenticationToken.Value))

        | HttpDigest ->
            // if it is None, then a request to the server must be done in order to retrieve the needed information
            if _httpDigestInfo.IsSome then
                let digestToken = 
                    HttpDigestAuthenticationUtility.getHttpDigestAuthenticationString(
                        httpRequest, 
                        _httpDigestInfo.Value, 
                        _settings.Authentication.Username, 
                        _settings.Authentication.Password
                    )

                httpRequest.Headers.Add(new HttpHeader(Name="Authorization", Value=digestToken))
        
        | Bearer ->
            // add the given token
            let token = String.Format("Bearer {0}", _settings.Authentication.Token)
            httpRequest.Headers.Add(new HttpHeader(Name="Authorization", Value=token))

        | WebForm ->
            // this kind of authentication is handled after that the request was done            
            ()
        
        | NoAuthentication ->
            // no authentication header needs to be created
            ()

    let isRequestOkToBeSentViaSelenium(httpRequest: HttpRequest) =
        if _settings.UseJavascriptEngineForRequest && httpRequest.Source.IsSome then
            let extension = Path.GetExtension(httpRequest.Uri.AbsolutePath)
            let refererHeader = HttpUtility.tryGetHeader("Referer", httpRequest.Headers)

            let mutable isPostDataInValidForm = true
            if String.IsNullOrEmpty(httpRequest.Data) && httpRequest.Method = HttpMethods.Post then
                isPostDataInValidForm <- httpRequest.Data =~ "(.*?)=(.*?)(&(.*?)=(.*?))*"

            let mutable mayNeedPostProcessing = true
            if httpRequest.Method = HttpMethods.Get && httpRequest.Source.IsSome then
                // simple link with no events doesn't need to be processed via Selenium
                mayNeedPostProcessing <- not <| String.IsNullOrWhiteSpace(httpRequest.Source.Value.Info)
            
            mayNeedPostProcessing &&
            isPostDataInValidForm &&
            not(_settings.StaticExtensions.Contains(extension)) && 
            refererHeader.IsSome &&
            Uri.IsWellFormedUriString(refererHeader.Value.Value, UriKind.Absolute)
        else false

    let initializeSelenium() =
        lock _seleniumDriverSyncRoot (fun () ->
            match _seleniumDriver with
            | Some _ -> ()
            | None ->
                _seleniumDriver <- Some(new SeleniumDriver(logProvider))
                _seleniumDriver.Value.ProxyUrl <- _settings.ProxyUrl
                _seleniumDriver.Value.Initialize()
        )
            
    do
        ServicePointManager.DefaultConnectionLimit <- Int32.MaxValue
        ServicePointManager.CheckCertificateRevocationList <- false
        ServicePointManager.Expect100Continue <- false
        ServicePointManager.ServerCertificateValidationCallback <- new RemoteCertificateValidationCallback(doCertificateValidation)
        logProvider.AddLogSourceToLoggers(_logger)

    new(defaultSettings: HttpRequestorSettings, logProvider: ILogProvider) = new DefaultHttpRequestor(defaultSettings, (fun _ -> ()), logProvider)
    
    member this.CertificationValidate = _certificationValidate.Publish
    member val SessionState : SessionStateManager option = None with get, set
    member this.Settings = _settings

    member this.DownloadData(httpRequest: HttpRequest) =
        requestNotificationCallback(httpRequest, false)
        let httpResponseResult: HttpResponse option ref = ref(None)

        try                
            applySettingsToRequest(httpRequest)
            applyAuthenticationInfoToRequestIfNeeded(httpRequest)
                
            // set the session parameters
            if this.SessionState.IsSome then
                this.SessionState.Value.ApplySessionParametersToRequest(httpRequest)

            let httpWebRequest = HttpRequestorUtility.createHttpWebRequest(_settings, httpRequest)
            
            // retrieve the buffer response
            use httpWebResponse = httpWebRequest.GetResponse() :?> HttpWebResponse
            use httpResponseStream = httpWebResponse.GetResponseStream()

            // read all data stream
            use destStream = new MemoryStream()
            httpResponseStream.CopyTo(destStream)
            destStream.ToArray()
        with
        | e -> 
            _logger.RequestError(httpRequest.Uri.ToString(), e.Message)
            Array.empty<Byte>

    member private this.SendRequestAsync(httpRequest: HttpRequest) =         
        async {
            requestNotificationCallback(httpRequest, false)

            use! holder = _requestGate.AsyncAcquire(_requestGateTimeout)
            let httpResponseResult: HttpResponse option ref = ref(None)

            try                
                applySettingsToRequest(httpRequest)
                applyAuthenticationInfoToRequestIfNeeded(httpRequest)
                applyFixToUnescapedCharacters(httpRequest)
                
                // set the session parameters
                if this.SessionState.IsSome then
                    this.SessionState.Value.ApplySessionParametersToRequest(httpRequest)
                                    
                // finaly send the request, first try via Selenium
                if isRequestOkToBeSentViaSelenium(httpRequest) then
                    initializeSelenium()

                    let refererHeader = HttpUtility.tryGetHeader("Referer", httpRequest.Headers)
                    let refererPath = new Uri(httpRequest.Uri, HttpUtility.getAbsolutePathDirectory(new Uri(refererHeader.Value.Value)))
                    
                    let file = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "Network", "Javascript", "SendRequest.js")
                    let jsSrc = File.ReadAllText(file)
                    let jsArgs = 
                        [
                            ("source", refererPath.AbsoluteUri)
                            ("elementId",  httpRequest.Source.Value.ElementId)
                            ("url", httpRequest.Uri.AbsoluteUri)
                            ("data", httpRequest.Data)
                        ] |> dict |> System.Collections.Generic.Dictionary
                    
                    match _seleniumDriver.Value.ExecuteScript(httpRequest, jsSrc, jsArgs) with
                    | Some result when result.ContainsKey("html") -> 
                        let html = result.["html"].ToString()
                        httpResponseResult := Some <| new HttpResponse(Html = html, StatusCode = HttpStatusCode.OK)
                        (!httpResponseResult).Value.Cookies.AddRange(result.["cookies"] :?> List<Cookie>)
                        (!httpResponseResult).Value.Headers.Add(new HttpHeader(Name = "Content-Type", Value ="text/html"))
                                                
                        if result.ContainsKey("network") then
                            // TODO: build the response. Currently this is not supported since Chrome headless doesn't support
                            // extension. An an extension is necessary in order to log the requests and responses.
                            // when and if supported I have to fill also all the other HttpResponse properties.
                            ()
                        
                    | _ -> ()      

                // if the request via PhantomJS fails try the standard way
                if (!httpResponseResult).IsNone then
                    let httpWebRequest = HttpRequestorUtility.createHttpWebRequest(_settings, httpRequest)

                    // retrieve the response Async
                    let! httpResponse = httpWebRequest.AsyncGetResponse()
                    use asyncReader = new FSharpx.Control.AsyncStreamReader(httpResponse.GetResponseStream(), Encoding.UTF8)                
                    let! html = asyncReader.ReadToEnd()
                                
                    // convert the http response to my object    
                    use httpWebResponse = httpResponse :?> HttpWebResponse               
                    httpResponseResult := Some <| HttpRequestorUtility.convertToHttpResponse(httpWebResponse, html)

                // check if is needed an authentication
                httpResponseResult := this.VerifyIfIsNeededToAuthenticate(httpRequest, !httpResponseResult)
                
                // retrieve the session response parameters
                if this.SessionState.IsSome && (!httpResponseResult).IsSome then
                    this.SessionState.Value.RetrieveSessionParametersFromResponse(httpRequest, (!httpResponseResult).Value)
            with
            | :? WebException as webException ->
                if webException.Response <> null then
                    // set the http response html
                    let httpResponse = webException.Response :?> HttpWebResponse

                    use streamReader = new StreamReader(httpResponse.GetResponseStream())                                        
                    use asyncReader = new FSharpx.Control.AsyncStreamReader(httpResponse.GetResponseStream())
                    let! html = asyncReader.ReadToEnd()

                    httpResponseResult := Some <| HttpRequestorUtility.convertToHttpResponse(httpResponse, html)
                    httpResponse.Dispose()

                    // check if is needed an authentication
                    httpResponseResult := this.VerifyIfIsNeededToAuthenticate(httpRequest, !httpResponseResult)
                else
                    _logger.RequestError(httpRequest.Uri.ToString(), webException.Message)
            | e -> 
                _logger.RequestError(httpRequest.Uri.ToString(), e.Message)

            requestNotificationCallback(httpRequest, true)
            return !httpResponseResult
        }

    member this.SendRequest(httpRequest: HttpRequest) =        
        try
            this.SendRequestAsync(httpRequest)
            |> Async.RunSynchronously
        with _ -> None

    member private this.VerifyIfIsNeededToAuthenticate(httpRequest: HttpRequest, httpResponse: HttpResponse option) =
        if not _skipAuthenticationProcess then
            let savedValue = _skipAuthenticationProcess
            _skipAuthenticationProcess <- true

            let httpResponseResult = ref httpResponse
                          
            match _settings.Authentication.Type with
            | HttpDigest -> 
                if httpResponse.IsSome && _httpDigestInfo.IsNone && httpResponse.Value.StatusCode = HttpStatusCode.Unauthorized then
                    // retrieve the Auth digest info and re-send the request with the correct token
                    _httpDigestInfo <- HttpDigestAuthenticationUtility.retrieveAuthenticationInfo(httpResponse.Value)
                    httpResponseResult := this.SendRequest(httpRequest)
      
            | WebForm ->
                (*
                if httpResponse.IsSome then
                    if webFormAuthDesc.IsLoggedOut(httpResponse.Value) then
                        if not <| webFormAuthDesc.TryAuthenticate(this.SendRequest) then
                            _logger.UnableToAuthenticate(httpRequest.Uri.ToString(), webFormAuthDesc)
                        else
                            // repeat the request with the authentication cookie
                            httpResponseResult := this.SendRequest(httpRequest)
                            *)
                ()
            | _ -> 
                // no authentication process needed
                ()

            _skipAuthenticationProcess <- savedValue
            !httpResponseResult
        
        else
            httpResponse

    member this.Dispose() =
        if _seleniumDriver.IsSome then
            _seleniumDriver.Value.Dispose()

    interface IHttpRequestor with

        member this.SendRequest(httpRequest: HttpRequest) =
            this.SendRequest(httpRequest)

        member this.CertificationValidate
            with get() = this.CertificationValidate

        member this.SessionState
            with get() = this.SessionState
            and set(v) = this.SessionState <- v

        member this.Settings 
            with get() = this.Settings

        member this.DownloadData(httpRequest: HttpRequest) =
            this.DownloadData(httpRequest)

    interface IDisposable with
        member this.Dispose() =
            this.Dispose()
            