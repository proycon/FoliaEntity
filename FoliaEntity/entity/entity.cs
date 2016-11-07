﻿using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using VDS.RDF;
using VDS.RDF.Writing;
using VDS.RDF.Parsing;
using System.Net;
using System.Xml;
using System.Web;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;


namespace FoliaEntity {
  public class entity {
    // =========================================================================================================
    // Name: entity
    // Goal: This module implements Named Entity Linking
    // History:
    // 24/oct/2016 ERK Created
    // ========================================== LOCAL VARIABLES ==============================================
    private String sApiStart = "http://spotlight.sztaki.hu:2232/rest/";
    private String sApiLotus = "http://lotus.lodlaundromat.org/retrieve/";
    private String sApiFlask = "http://flask.fii800.lod.labs.vu.nl";
    private int iBufSize = 1024;        // Buffer size
    private string strNs = "";          // Possible namespace URI
    private ErrHandle errHandle;        // Our own copy of the error handle
    private String loc_sEntity = "";    // The Named Entity string
    private String loc_sClass = "";     // The kind of NE (loc, per etc)
    private String loc_sSent = "";      // The sentence
    private String loc_sOffset = "";    // Offset as a string
    private String loc_sId = "";        // The id of the sentence
    private String loc_sRequest = "";   // Kind of request: disambiguate or annotate
    private int loc_iHits = 0;
    private int loc_iFail = 0;
    private List<link> loc_lstLinks = null; // Resulting links
    private String loc_sReqModel = "<annotation text=''><surfaceForm name='' offset='' /></annotation>";
    private String loc_sRespModel = 
      "<?xml version='1.0' encoding='utf-8'?><Annotation><Resources>" + 
      "<Resource service='' URI='' support='' types='' surfaceForm='' offset='' similarityScore='' percentageOfSecondRank='' />" + 
      "</Resources></Annotation>";
    private Regex regHref = new Regex("(href=['\"]?)([^'\"]+)");
    private bool bDebug = false;        // Debugging set or not
    // =========================================================================================================
    public entity(ErrHandle objErr, String sEntity, String sClass, String sSent, String sOffset, String sId) {
      this.errHandle = objErr;
      this.loc_sClass = sClass;
      this.loc_sEntity = sEntity;
      this.loc_sId = sId;
      this.loc_sOffset = sOffset;
      this.loc_sSent = sSent;
      // Reset counters
      this.loc_iFail = 0;
      this.loc_iHits = 0;
    }

    // ================== GETTERS and SETTERS =================================================================
    public List<link> get_links() { return this.loc_lstLinks; }
    public int get_hits() { return this.loc_iHits; }
    public int get_fail() { return this.loc_iFail; }
    public void set_debug(bool bSet) { this.bDebug = bSet; }

    /* -------------------------------------------------------------------------------------
     * Name:        oneEntityToLinks
     * Goal:        Try to provide a link to the NE we have stored
     * Parameters:  sConfidence     - Minimal level of confidence that should be met
     * History:
     * 24/oct/2016 ERK Created
       ------------------------------------------------------------------------------------- */
    public bool oneEntityToLinks(String sConfidence) {
      try {
        // Initialize the return list
        this.loc_lstLinks = new List<link>();

        // Try making a disambiguation SPOTLIGHT request
        bool bResult = this.oneSpotlightRequest("disambiguate", sConfidence, ref loc_lstLinks);
        if(!bResult) {
          // Give it another go: try the 'annotate' method
          bResult = this.oneSpotlightRequest("annotate", sConfidence, ref loc_lstLinks);
        }

        // Add a LOTUS request
        bool bLotus = this.oneLotusRequest(sConfidence, ref loc_lstLinks);
        // Possibly adapt the result boolean
        if (!bResult) bResult = bLotus;

        // Return what we found
        return bResult;
      } catch (Exception ex) {
        errHandle.DoError("entity/oneEntityToLinks", ex); // Provide standard error message
        return false;
      }
    }

    /* -------------------------------------------------------------------------------------
     * Name:        docEntityToLinks
     * Goal:        Find all links of one document in one go
     * Parameters:  sDocText    - Text of the whole document
     *              lstEntExpr  - List of named entities
     *              lstEntOffset- List of offsets of the entities within [sDocText]
     *              lstEntLink  - Resulting list of entity links
     * History:
     * 7/nov/2016 ERK Created
       ------------------------------------------------------------------------------------- */
    public bool docEntityToLinks(String sDocText, List<String> lstEntExpr, List<int> lstEntOffset, ref List<link> lstEntLink) {
      bool bResult = true;

      try {
        // Clear the results
        lstEntLink.Clear();

        // Create a FLASK request
        bool bFlask = this.oneFlaskRequest(sDocText, lstEntExpr, lstEntOffset, ref loc_lstLinks);

        // Process the answer
        if (bFlask) {
          lstEntLink = loc_lstLinks;
        }

        // Return what we found
        return bResult;
      } catch (Exception ex) {
        errHandle.DoError("entity/docEntityToLinks", ex); // Provide standard error message
        return false;
      }
    }


    /* -------------------------------------------------------------------------------------
     * Name:        oneSpotlightRequest
     * Goal:        Perform one request to SPOTLIGHT
     * Parameters:  sMethod     - Either 'disambiguate' or 'annotate'
     *              sConfidence - Minimal level of confidence that should be met
     *              lstLinks    - List of 'link' objects
     * History:
     * 24/oct/2016 ERK Created
       ------------------------------------------------------------------------------------- */
    private bool oneSpotlightRequest(String sMethod, String sConfidence, ref List<link> lstLinks) {
      String sXmlPost = "";
      String sData = "";
      XmlWriterSettings wrSet = new XmlWriterSettings();

      try {

        // Action depends on the type of request
        switch(sMethod) {
          case "annotate":
            sXmlPost = this.loc_sEntity;
            break;
          case "disambiguate":
            // Create a data XML object
            XmlDocument pdxData = new XmlDocument();
            pdxData.LoadXml(loc_sReqModel);
            // Fill in the variables in this XML document
            XmlNode ndxSurface = pdxData.SelectSingleNode("./descendant::surfaceForm");
            ndxSurface.Attributes["name"].Value = this.loc_sEntity;
            ndxSurface.Attributes["offset"].Value = this.loc_sOffset;
            XmlNode ndxAnn = pdxData.SelectSingleNode("./descendant-or-self::annotation");
            // Take the text, but make sure that the quotation marks " are changed to single quotes
            ndxAnn.Attributes["text"].Value = this.loc_sSent;   // .Replace("\"", "'");
            // ndxAnn.Attributes["text"].Value = System.Security.SecurityElement.Escape(this.loc_sSent);
            // Convert the xml document to a string
            wrSet.OmitXmlDeclaration = true;
            wrSet.Encoding = Encoding.UTF8;
            using (var stringWriter = new System.IO.StringWriter())
              using (var xmlTextWriter = XmlWriter.Create(stringWriter, wrSet)) {
              pdxData.WriteTo(xmlTextWriter);
              xmlTextWriter.Flush();
              sXmlPost = stringWriter.GetStringBuilder().ToString();
            }
            // Do escaping
            // sXmlPost = pdxData.OuterXml;
            // sXmlPost = System.Security.SecurityElement.Escape(sXmlPost);
            // sXmlPost = Uri.EscapeUriString(sXmlPost);
            break;
        }

        // Make sure URL encoding is done for the XmlPost
        // sXmlPost = HttpUtility.UrlEncode(HttpUtility.UrlDecode(sXmlPost), Encoding.UTF8);

        // Prepare the POST string to be sent
        //NameValueCollection oQueryString = HttpUtility.ParseQueryString(String.Empty, Encoding.UTF8);
        //oQueryString.Add("confidence", sConfidence);
        //oQueryString.Add("text", sXmlPost);
        //// sData = Uri.EscapeUriString(HttpUtility.UrlDecode(oQueryString.ToString()));
        //sData = oQueryString.ToString();
        sData = "confidence=" + sConfidence + "&text=" + HttpUtility.UrlEncode(sXmlPost, Encoding.UTF8);

        // Make a request
        XmlDocument pdxReply = MakeXmlPostRequest(sApiStart, sMethod, sData);
        if (pdxReply == null) {
          // Try to get a reply from the HTML
          pdxReply = MakeHtmlPostRequest(sApiStart, sMethod, sData, this.loc_sEntity);
        }

        // Check the reply and process it
        if (pdxReply != null) {
          // Find a list of all <Resource> answers
          XmlNodeList lstResources = pdxReply.SelectNodes("./descendant::Resource");
          for (int i=0;i<lstResources.Count;i++) {
            // Get access to this resource
            XmlNode resThis = lstResources[i];
            // Get the kind of web service used
            String sService = "spotlight";
            if (resThis.Attributes["service"] != null)
              sService = resThis.Attributes["service"].Value;
            // Calculate 'found' and 'classmatch'
            String eClass = this.loc_sClass;
            String resType = resThis.Attributes["types"].Value;
            String sClassMatch = "no";
            String sHit = "";
            bool bFound = false;
            switch (eClass) {
              case "loc":   // location
                if (resType == "" || resType.Contains(":Place")) { bFound = true; sClassMatch = "yes"; }
                break;
              case "org":   // organization
                if (resType == "" || resType.Contains("Organization") || resType.Contains("Organisation")) { bFound = true; sClassMatch = "yes"; }
                break;
              case "pro":   // product
                if (resType == "" || resType.Contains(":Language")) { bFound = true; sClassMatch = "yes"; }
                break;
              case "per":   // person
                if (resType == "" || resType.Contains(":Agent")) { bFound = true; sClassMatch = "yes"; }
                break;
              case "misc":  // miscellaneous
                bFound = true; sClassMatch = "misc";
                break;
              default:      // Anything else
                if (resType == "") { bFound = true; sClassMatch = "empty"; }
                break;
            }

            // Do we have a hit?
            if (bFound) {
              this.loc_iHits++; sHit = "true";
            } else {
              this.loc_iFail++; sHit = "false";
            }

            // Create a link object
            link oLink = new link(sService, sMethod,
              resThis.Attributes["URI"].Value,
              resThis.Attributes["surfaceForm"].Value, 
              resThis.Attributes["types"].Value,
              sClassMatch, 
              resThis.Attributes["support"].Value, 
              resType,
              resThis.Attributes["similarityScore"].Value, 
              resThis.Attributes["percentageOfSecondRank"].Value,
              sHit);
            // Add the link object to the list of what is returned
            lstLinks.Add(oLink);

            // ================ DEBUG ===============
            //if (this.loc_sEntity.Contains("Vlaanderen")) {
            //  int j = 0;
            //}
            // ======================================

          }
        }

        // Be positive
        return true;
      } catch (Exception ex) {
        errHandle.DoError("entity/oneSpotlightRequest", ex); // Provide standard error message
        return false;
      }

    }

    /* -------------------------------------------------------------------------------------
     * Name:        oneFlaskRequest
     * Goal:        Perform one request to SPOTLIGHT
     * 
     * Parameters:  sDocText    - Text of the whole document
     *              lstEntExpr  - List of named entities
     *              lstEntOffset- List of offsets of the entities within [sDocText]
     *              lstLinks    - List of 'link' objects (returned from us)
     * History:
     * 2/nov/2016 ERK Created
       ------------------------------------------------------------------------------------- */
    private bool oneFlaskRequest(String sDocText, List<String> lstEntExpr, List<int> lstEntOffset, ref List<link> lstLinks) {
      String sRdfPost = "";
      String sData = "";
      XmlWriterSettings wrSet = new XmlWriterSettings();

      try {
        // TEST: load the data from stored example.rdf
        // sRdfPost = File.ReadAllText(@"D:\Data Files\TG\writable\example.rdf");
        // turtle oTurtle = new turtle(errHandle, loc_sEntity, Int32.Parse(loc_sOffset), loc_sSent);

        // PRODUCTION: load the data from the information provided
        turtle oTurtle = new turtle(errHandle, sDocText, lstEntExpr, lstEntOffset);
        sRdfPost = oTurtle.create();
        // NOTE: do not perform URL encoding
        sData = sRdfPost;

        // Make a request
        XmlDocument pdxReply = MakeXmlPostRequest(sApiFlask, "", sData);

        // Check the reply and process it
        if (pdxReply != null) {
          // Find a list of all <Resource> answers
          XmlNodeList lstResources = pdxReply.SelectNodes("./descendant::Resource");
          for (int i = 0; i < lstResources.Count; i++) {
            // Get access to this resource
            XmlNode resThis = lstResources[i];
            // Get the kind of web service used
            String sService = "flask";
            if (resThis.Attributes["service"] != null)
              sService = resThis.Attributes["service"].Value;
            // Calculate 'found' and 'classmatch'
            String eClass = this.loc_sClass;
            String resType = resThis.Attributes["types"].Value;
            String sClassMatch = "no";
            String sHit = "";
            bool bFound = false;
            switch (eClass) {
              case "loc":   // location
                if (resType == "" || resType.Contains(":Place")) { bFound = true; sClassMatch = "yes"; }
                break;
              case "org":   // organization
                if (resType == "" || resType.Contains("Organization") || resType.Contains("Organisation")) { bFound = true; sClassMatch = "yes"; }
                break;
              case "pro":   // product
                if (resType == "" || resType.Contains(":Language")) { bFound = true; sClassMatch = "yes"; }
                break;
              case "per":   // person
                if (resType == "" || resType.Contains(":Agent")) { bFound = true; sClassMatch = "yes"; }
                break;
              case "misc":  // miscellaneous
                bFound = true; sClassMatch = "misc";
                break;
              default:      // Anything else
                if (resType == "") { bFound = true; sClassMatch = "empty"; }
                break;
            }

            // Do we have a hit?
            if (bFound) {
              this.loc_iHits++; sHit = "true";
            } else {
              this.loc_iFail++; sHit = "false";
            }

            // Create a link object
            link oLink = new link(sService, "",
              resThis.Attributes["URI"].Value,
              resThis.Attributes["surfaceForm"].Value,
              resThis.Attributes["types"].Value,
              sClassMatch,
              resThis.Attributes["support"].Value,
              resType,
              resThis.Attributes["similarityScore"].Value,
              resThis.Attributes["percentageOfSecondRank"].Value,
              sHit);
            // Add the link object to the list of what is returned
            lstLinks.Add(oLink);

            // ================ DEBUG ===============
            //if (this.loc_sEntity.Contains("Vlaanderen")) {
            //  int j = 0;
            //}
            // ======================================

          }
        }

        // Be positive
        return true;
      } catch (Exception ex) {
        errHandle.DoError("entity/oneFlaskRequest", ex); // Provide standard error message
        return false;
      }

    }

    /* -------------------------------------------------------------------------------------
     * Name:        oneLotusRequest
     * Goal:        Perform one request to SPOTLIGHT
     * Parameters:  sConfidence - Minimal level of confidence that should be met
     *              lstLinks    - List of 'link' objects
     * History:
     * 2/nov/2016 ERK Created
       ------------------------------------------------------------------------------------- */
    private bool oneLotusRequest(String sConfidence, ref List<link> lstLinks) {
      String sXmlPost = "";
      String sMethod = "";
      String sData = "";
      XmlWriterSettings wrSet = new XmlWriterSettings();

      try {
        // Prepare the data for the post request
        sData = "string=" + HttpUtility.UrlEncode(this.loc_sEntity, Encoding.UTF8) + 
                "&match=fuzzyconjunct&rank=lengthnorm&predicate=label&uniq=true&langtag=nl";

        // Make a request
        XmlDocument pdxReply = MakeXmlPostRequest(sApiLotus, sMethod, sData);

        // Preliminary check of Lotus results
        if (pdxReply != null && pdxReply.SelectSingleNode("./descendant::Resource").Attributes["URI"].Value == "") {
          // Provide one alternative
          String sDataAlternative = "string=" + HttpUtility.UrlEncode(this.loc_sEntity, Encoding.UTF8) +
                  "&match=fuzzyconjunct&rank=lengthnorm&uniq=true&langtag=nl";
          // Make an alternative request
          pdxReply = MakeXmlPostRequest(sApiLotus, sMethod, sDataAlternative);
        }

        // Check the reply and process it
        if (pdxReply != null && pdxReply.SelectSingleNode("./descendant::Resource").Attributes["URI"].Value != "") {
          // Find a list of all <Resource> answers
          XmlNodeList lstResources = pdxReply.SelectNodes("./descendant::Resource");
          for (int i = 0; i < lstResources.Count; i++) {
            // Get access to this resource
            XmlNode resThis = lstResources[i];
            // Calculate 'found' and 'classmatch'
            String eClass = this.loc_sClass;
            String resType = resThis.Attributes["types"].Value;
            String sClassMatch = "no";
            String sHit = "";
            bool bFound = false;
            switch (eClass) {
              case "loc":   // location
                if (resType == "" || resType.Contains(":Place")) { bFound = true; sClassMatch = "yes"; }
                break;
              case "org":   // organization
                if (resType == "" || resType.Contains("Organization") || resType.Contains("Organisation")) { bFound = true; sClassMatch = "yes"; }
                break;
              case "pro":   // product
                if (resType == "" || resType.Contains(":Language")) { bFound = true; sClassMatch = "yes"; }
                break;
              case "per":   // person
                if (resType == "" || resType.Contains(":Agent")) { bFound = true; sClassMatch = "yes"; }
                break;
              case "misc":  // miscellaneous
                bFound = true; sClassMatch = "misc";
                break;
              default:      // Anything else
                if (resType == "") { bFound = true; sClassMatch = "empty"; }
                break;
            }

            // Do we have a hit?
            if (bFound) {
              this.loc_iHits++; sHit = "true";
            } else {
              this.loc_iFail++; sHit = "false";
            }

            // Create a link object
            link oLink = new link("lotus", sMethod,
              resThis.Attributes["URI"].Value,
              resThis.Attributes["surfaceForm"].Value,
              resThis.Attributes["types"].Value,
              sClassMatch,
              resThis.Attributes["support"].Value,
              resType,
              resThis.Attributes["similarityScore"].Value,
              resThis.Attributes["percentageOfSecondRank"].Value,
              sHit);
            // Should we add this?
            if (lstLinks.Count == 0 || (lstLinks.Count>0 && lstLinks[0].uri != oLink.uri && oLink.uri != "")) {
              // Add the link object to the list of what is returned
              lstLinks.Add(oLink);
            }

          }
        }

        // Be positive
        return true;
      } catch (Exception ex) {
        errHandle.DoError("entity/oneLotusRequest", ex); // Provide standard error message
        return false;
      }

    }

    /* -------------------------------------------------------------------------------------
     * Name:        MakeXmlPostRequest
     * Goal:        Issue a POST request  that expects an XML answer
     * Parameters:  sMethod     - Either 'disambiguate' or 'annotate'
     *              sConfidence - Minimal level of confidence that should be met
     *              lstLinks    - List of 'link' objects
     * History:
     * 24/oct/2016 ERK Created
       ------------------------------------------------------------------------------------- */
    private XmlDocument MakeXmlPostRequest(String sUrlStart, String sMethod, String sData) {
      String sReturnLanguage = "";
      byte[] postBytes;

      try {
        // Create the request string
        String sRequest = sUrlStart + sMethod.ToLower();

        HttpWebRequest request = null;
        // Set the method correctly
        if (sUrlStart.ToLower().Contains("lotus")) {
          // Create a request
          request = (HttpWebRequest)WebRequest.Create(sRequest + "?" + sData);
          request.Method = "GET";
          request.ContentType = "application/x-www-form-urlencoded";
          request.Accept = "application/json";
          sReturnLanguage = "json";
        } else if (sUrlStart.ToLower().Contains("flask")) {
          // Prepare the data 
          postBytes = (new UTF8Encoding()).GetBytes(sData.ToString());
          // Create a request and expect XML response
          request = (HttpWebRequest)WebRequest.Create(sRequest);
          request.Method = "POST";
          request.ContentLength = postBytes.Length;
          request.ContentType = "application/x-www-form-urlencoded";
          request.Accept = "*/*";
          request.Timeout = 500000; // 500,000 milliseconds = 500 seconds = 
          sReturnLanguage = "rdf";

          Stream dataStream = request.GetRequestStream();
          // Write the data to the request stream
          dataStream.Write(postBytes, 0, postBytes.Length);
          dataStream.Close();
        } else {
          // Prepare the data
          ASCIIEncoding ascii = new ASCIIEncoding();
          postBytes = ascii.GetBytes(sData.ToString());

          // Create a request and expect XML response
          request = (HttpWebRequest)WebRequest.Create(sRequest);
          request.Method = "POST";
          request.ContentLength = postBytes.Length;
          request.ContentType = "application/x-www-form-urlencoded";
          request.Accept = "text/xml";
          sReturnLanguage = "xml";

          // Is this a HTTPS request?
          if (sUrlStart.StartsWith("https")) {
            ServicePointManager.ServerCertificateValidationCallback =
              new System.Net.Security.RemoteCertificateValidationCallback(AcceptAllCertifications);
          }

          Stream dataStream = request.GetRequestStream();
          // Write the data to the request stream
          dataStream.Write(postBytes, 0, postBytes.Length);
          dataStream.Close();
        }

        // Get a response
        HttpWebResponse response = null;
        String sReply = "";
        try {
          // Try to get a response
          response = (HttpWebResponse)request.GetResponse();
        } catch (Exception e) {
          if (this.bDebug) {
            errHandle.Status("MakeXmlPostRequest does not work; Trying Html request instead.");
          }
          return null;
        }
        // Process the result: get it as a string
        sReply = readResponse(ref response);
        XmlDocument pdxReply = new XmlDocument();
        // Processing depends on the type
        switch (sReturnLanguage) {
          case "xml":
            // Convert the XML reply to a processable object
            pdxReply.LoadXml(sReply);
            break;
          case "json":
            LotusResponse oLotus = Newtonsoft.Json.JsonConvert.DeserializeObject<LotusResponse>(sReply);
            // Pre-load a response
            pdxReply.LoadXml(loc_sRespModel);
            // Find the correct node
            XmlNode ndxResource = pdxReply.SelectSingleNode("./descendant::Resource[1]");
            // Sanity check
            if (ndxResource != null) {
              // Do we actually have results?
              if (oLotus != null && oLotus.numhits>0) {
                // Fill in the values of this node
                LotusHit oHit = oLotus.hits[0];  // Take the first hit
                ndxResource.Attributes["service"].Value = "lotus";
                ndxResource.Attributes["URI"].Value = oHit.subject;
                ndxResource.Attributes["support"].Value = "1";
                ndxResource.Attributes["types"].Value = "";
                ndxResource.Attributes["surfaceForm"].Value = this.loc_sEntity;
                ndxResource.Attributes["offset"].Value = "0";
                ndxResource.Attributes["similarityScore"].Value = "1.0";
                ndxResource.Attributes["percentageOfSecondRank"].Value = "0.0";
              }
            }
            break;
          case "rdf":
            break;
        }
        // Return the XML document
        return pdxReply;
      } catch (Exception e) {
        errHandle.DoError("entity/MakeXmlPostRequest", e); // Provide standard error message
        return null;
      }
    }

    /* -------------------------------------------------------------------------------------
     * Name:        MakeHtmlPostRequest
     * Goal:        Issue a POST request  that expects a HTML answer
     * Parameters:  sMethod     - Either 'disambiguate' or 'annotate'
     *              sConfidence - Minimal level of confidence that should be met
     *              lstLinks    - List of 'link' objects
     * History:
     * 24/oct/2016 ERK Created
       ------------------------------------------------------------------------------------- */
    private XmlDocument MakeHtmlPostRequest(String sUrlStart, String sMethod, String sData, String sEntity) {
      try {
        // Prepare the data
        ASCIIEncoding ascii = new ASCIIEncoding();
        byte[] postBytes = ascii.GetBytes(sData.ToString());

        // Create the request string
        String sRequest = sUrlStart + sMethod.ToLower();

        // Create a request
        HttpWebRequest request = (HttpWebRequest)WebRequest.Create(sRequest);
        // Set the method correctly
        request.Method = "POST";
        request.ContentLength = postBytes.Length;
        request.ContentType = "application/x-www-form-urlencoded";
        request.Accept = "text/html";

        Stream dataStream = request.GetRequestStream();
        // Write the data to the request stream
        dataStream.Write(postBytes, 0, postBytes.Length);
        dataStream.Close();

        // Get a response
        HttpWebResponse response = null;
        String sReply = "";
        try {
          response = (HttpWebResponse)request.GetResponse();
        } catch (Exception e) {
          errHandle.DoError("entity/MakeHtmlPostRequest", e); // Provide standard error message
          return null;
        }
        // Process the result: get it as a string
        String sHtml = readResponse(ref response);
        // Get the correct href= from the string using RE
        Match mcHtml = regHref.Match(sHtml);
        if (!mcHtml.Success || mcHtml.Groups.Count<3) {
          // No answer after all
          return null;
        }
        String sHref = mcHtml.Groups[2].Value;
        sReply = "<Resources><Resource URI='' support='0' types='' surfaceForm='' offset='0' similarityScore='1.0' percentageOfSecondRank='0.0' /></Resources>";
        // Convert the XML reply to a processable object
        XmlDocument pdxReply = new XmlDocument();
        pdxReply.LoadXml(sReply);
        // Add the URI
        XmlNode ndxRes = pdxReply.SelectSingleNode("./descendant::Resource[1]");
        ndxRes.Attributes["URI"].Value = sHref;
        ndxRes.Attributes["surfaceForm"].Value = sEntity;
        // Return the XML document
        return pdxReply;
      } catch (Exception e) {
        errHandle.DoError("entity/MakeHtmlPostRequest", e); // Provide standard error message
        return null;
      }
    }

    private String readResponse(ref HttpWebResponse response) {
      try {
        // Process the result: get it as a string
        StringBuilder sbReply = new StringBuilder();
        using (Stream strResponse = response.GetResponseStream())
        using (StreamReader rdThis = new StreamReader(strResponse)) {
          Char[] readBuff = new Char[iBufSize];
          int iCount = rdThis.Read(readBuff, 0, iBufSize);
          while (iCount > 0) {
            // Append the information to the stringbuilder
            sbReply.Append(new String(readBuff, 0, iCount));
            // Make a follow-up request
            iCount = rdThis.Read(readBuff, 0, iBufSize);
          }
        }
        // Return the result
        return sbReply.ToString();
      } catch (Exception e) {
        errHandle.DoError("entity/readResponse", e); // Provide standard error message
        return null;
      }
    }

    private bool AcceptAllCertifications(object sender, 
      System.Security.Cryptography.X509Certificates.X509Certificate certification, 
      System.Security.Cryptography.X509Certificates.X509Chain chain, 
      System.Net.Security.SslPolicyErrors sslPolicyErrors) {
      return true;
    }

  }

  public class link {
    public String service = "";                 // Kind of web service that has been used
    public String method = "";                  // Method used within the web service
    public String uri = "";
    public String form = "";
    public String type = "";
    public String classmatch = "";
    public String support = "";
    public String offset = "";
    public String similarityScore = "";
    public String percentageOfSecondRank = "";
    public String hit = "";
    public link(String sService, String sMethod, String sUri, String sForm, String sType, String sClassmatch, String sSupport, String sOffset, 
      String sSimilarityScore, String sPercentageOfSecondRank, String sHit) {
      this.service = sService;
      this.method = sMethod;
      this.uri = sUri;
      this.form = sForm;
      this.classmatch = sClassmatch;
      this.support = sSupport;
      this.offset = sOffset;
      this.similarityScore = sSimilarityScore;
      this.percentageOfSecondRank = sPercentageOfSecondRank;
      this.hit = sHit;
    }

    public String toCsv() {
      // Return all the elements, but make sure the HIT (boolean) is first
      return this.hit + "\t" + this.service + "\t" + this.method + "\t" + this.uri + "\t" + this.form + "\t" + 
        this.classmatch + "\t" + this.support +
        this.offset + "\t" + this.similarityScore + "\t" + this.percentageOfSecondRank;
    }
  }

  public class LotusHit {
    public String subject;
    public String predicate;
    public String sTring;
    public String langtag;
    public String docid;
    public long timestamp;
    public double tr;
    public double sr;
    public double length;
    public int r2d;
    public int degree;
  }
  public class LotusResponse {
    public int took;
    public int numhits;
    public int returned;
    public LotusHit[] hits;
  }

  public class turtleItem {
    public String text;  // The text of the item
    public int offset;   // The offset of the item within the context
    public turtleItem(String sText, int iOffset) {
      this.text = sText;
      this.offset = iOffset;
    }
  }
  public class turtle {
    public List<turtleItem> lstItem;  // Array of items within the context
    public String context;            // The context for the request
    public ErrHandle errHandle;       // Own copy of error handler for error communication
    public String sLanguage = "nl";   // Default language
    private IGraph g = new Graph();   // Required graph
    private String loc_sModel = 
      "<?xml version='1.0' encoding='UTF-8'?>" +
      "<rdf:RDF" +
      "   xmlns:nif='http://persistence.uni-leipzig.org/nlp2rdf/ontologies/nif-core#'" +
      "   xmlns:rdf='http://www.w3.org/1999/02/22-rdf-syntax-ns#'" +
      ">" +
      "  <rdf:Description rdf:about='http://www.aksw.org/gerbil/NifWebService/request_0#char=0,1'>" +
      "    <rdf:type rdf:resource='http://persistence.uni-leipzig.org/nlp2rdf/ontologies/nif-core#RFC5147String'/>" +
      "    <rdf:type rdf:resource='http://persistence.uni-leipzig.org/nlp2rdf/ontologies/nif-core#String'/>" +
      "    <nif:endIndex rdf:datatype='http://www.w3.org/2001/XMLSchema#nonNegativeInteger'>1</nif:endIndex>" +
      "    <rdf:type rdf:resource='http://persistence.uni-leipzig.org/nlp2rdf/ontologies/nif-core#Phrase'/>" +
      "    <nif:beginIndex rdf:datatype='http://www.w3.org/2001/XMLSchema#nonNegativeInteger'>0</nif:beginIndex>" +
      "    <nif:lang rdf:datatype='http://www.w3.org/2001/XMLSchema#string'>nl</nif:lang>" +
      "    <nif:anchorOf rdf:datatype='http://www.w3.org/2001/XMLSchema#string'>-</nif:anchorOf>" +
      "    <nif:referenceContext rdf:resource='http://www.aksw.org/gerbil/NifWebService/request_0#char=0,1'/>" +
      "  </rdf:Description>" +
      "  <rdf:Description rdf:about='http://www.aksw.org/gerbil/NifWebService/request_0#char=0,1'>" +
      "    <rdf:type rdf:resource='http://persistence.uni-leipzig.org/nlp2rdf/ontologies/nif-core#String'/>" +
      "    <nif:beginIndex rdf:datatype='http://www.w3.org/2001/XMLSchema#nonNegativeInteger'>0</nif:beginIndex>" +
      "    <rdf:type rdf:resource='http://persistence.uni-leipzig.org/nlp2rdf/ontologies/nif-core#RFC5147String'/>" +
      "    <nif:endIndex rdf:datatype='http://www.w3.org/2001/XMLSchema#nonNegativeInteger'>1</nif:endIndex>" +
      "    <nif:predominantLanguage rdf:datatype='http://www.w3.org/2001/XMLSchema#string'>nl</nif:predominantLanguage>" +
      "    <nif:isString rdf:datatype='http://www.w3.org/2001/XMLSchema#string'>-</nif:isString>" +
      "    <rdf:type rdf:resource='http://persistence.uni-leipzig.org/nlp2rdf/ontologies/nif-core#Context'/>" +
      "  </rdf:Description>" +
      "</rdf:RDF>";

    public turtle(ErrHandle objErr, String sContext, List<String> lstEntExpr, List<int> lstEntOffset) {
      // Accept the parameters
      this.errHandle = objErr;
      this.lstItem = new List<turtleItem>();
      this.context = sContext;
      for (int i=0;i<lstEntExpr.Count;i++) {
        this.lstItem.Add(new turtleItem(lstEntExpr[i], lstEntOffset[i]));
      }
      // Convert this into RDF internally
      try {
        // Read the model into an Xml document
        XmlDocument pdxModel = new XmlDocument();
        pdxModel.LoadXml(this.loc_sModel);

        // Get the namespace managers required for this one
        XmlNamespaceManager nsModel = new XmlNamespaceManager(pdxModel.NameTable);
        nsModel.AddNamespace("nif", "http://persistence.uni-leipzig.org/nlp2rdf/ontologies/nif-core#");
        // nsModel.AddNamespace("nif", pdxModel.DocumentElement.NamespaceURI);
        nsModel.AddNamespace("rdf", "http://www.w3.org/1999/02/22-rdf-syntax-ns#");

        // Set the necessary values within this XML specification
        XmlNodeList ndxDescription = pdxModel.SelectNodes("./descendant::rdf:Description", nsModel);
        XmlNode ndxThis = null;
        XmlNode ndxWork = null;
        XmlNode ndxParent = pdxModel.SelectSingleNode("./descendant-or-self::rdf:RDF", nsModel);

        // The second <Description> node specifies the context
        ndxThis = ndxDescription.Item(1);
        // Set the start and end 
        ndxThis.Attributes["rdf:about"].Value = "http://www.aksw.org/gerbil/NifWebService/request_0#char=0," + sContext.Length;
        // Set the context string
        ndxWork = ndxThis.SelectSingleNode("./child::nif:isString", nsModel);
        ndxWork.InnerText = sContext;
        // Set the end index
        ndxWork = ndxThis.SelectSingleNode("./child::nif:endIndex", nsModel);
        ndxWork.InnerText = sContext.Length.ToString();

        // Process the first description node
        if (lstEntExpr.Count>0) {
          // Calculate start and end
          int iStart = lstEntOffset[0];
          String sItem = lstEntExpr[0];
          int iEnd = iStart + sItem.Length;

          // The first <Description> node specifies the string we are looking for
          ndxThis = ndxDescription.Item(0);
          // Set the start and end 
          ndxThis.Attributes["rdf:about"].Value = "http://www.aksw.org/gerbil/NifWebService/request_0#char=" +
            iStart + "," + iEnd;
          // Set the begin and end index
          ndxWork = ndxThis.SelectSingleNode("./child::nif:beginIndex", nsModel);
          ndxWork.InnerText = iStart.ToString();
          ndxWork = ndxThis.SelectSingleNode("./child::nif:endIndex", nsModel);
          ndxWork.InnerText = iEnd.ToString();
          // Set the item string
          ndxWork = ndxThis.SelectSingleNode("./child::nif:anchorOf", nsModel);
          ndxWork.InnerText = sItem;
          // Set the referencecontext
          ndxWork = ndxThis.SelectSingleNode("./child::nif:referenceContext", nsModel);
          ndxWork.Attributes["rdf:resource"].Value = "http://www.aksw.org/gerbil/NifWebService/request_0#char=0," + sContext.Length;

          // Prepare a copy of this node
          String sZeroNode = ndxThis.OuterXml;

          // Process all other entity expressions
          for (int i=1;i<lstEntExpr.Count;i++) {
            // Calculate start and end
            iStart = lstEntOffset[i];
            sItem = lstEntExpr[i];
            iEnd = iStart + sItem.Length;

            // Add the information from node 0
            XmlNode ndxCopy = ndxThis.CloneNode(true);
            XmlNode ndxNew = ndxParent.AppendChild(ndxCopy);

            // Set the start and end 
            ndxNew.Attributes["rdf:about"].Value = "http://www.aksw.org/gerbil/NifWebService/request_0#char=" +
              iStart + "," + iEnd;
            // Set the begin and end index
            ndxWork = ndxNew.SelectSingleNode("./child::nif:beginIndex", nsModel);
            ndxWork.InnerText = iStart.ToString();
            ndxWork = ndxNew.SelectSingleNode("./child::nif:endIndex", nsModel);
            ndxWork.InnerText = iEnd.ToString();
            // Set the item string
            ndxWork = ndxNew.SelectSingleNode("./child::nif:anchorOf", nsModel);
            ndxWork.InnerText = sItem;
            // Set the referencecontext
            ndxWork = ndxNew.SelectSingleNode("./child::nif:referenceContext", nsModel);
            ndxWork.Attributes["rdf:resource"].Value = "http://www.aksw.org/gerbil/NifWebService/request_0#char=0," + sContext.Length;
          }
        }


        // Load and read the xml text
        RdfXmlParser rdfParser = new RdfXmlParser();

        String sPdx = pdxModel.SelectSingleNode("./descendant-or-self::rdf:RDF", nsModel).OuterXml;

        rdfParser.Load(g, new StringReader(sPdx));


      } catch (Exception e) {
        errHandle.DoError("entity/turtle", e); // Provide standard error message
      }
    }

    public turtle(ErrHandle objErr, String sItem, int iOffset, String sContext) {
      // Accept the parameters
      this.errHandle = objErr;
      this.lstItem = new List<turtleItem>();
      this.lstItem.Add(new turtleItem(sItem, iOffset));
      this.context = sContext;
      // Convert this into RDF internally
      try {
        // Calculate start and end
        int iStart = iOffset;
        int iEnd = iOffset + sItem.Length;

        // Read the model into an Xml document
        XmlDocument pdxModel = new XmlDocument();
        pdxModel.LoadXml(this.loc_sModel);

        // Get the namespace managers required for this one
        XmlNamespaceManager nsModel = new XmlNamespaceManager(pdxModel.NameTable);
        nsModel.AddNamespace("nif", "http://persistence.uni-leipzig.org/nlp2rdf/ontologies/nif-core#");
        // nsModel.AddNamespace("nif", pdxModel.DocumentElement.NamespaceURI);
        nsModel.AddNamespace("rdf", "http://www.w3.org/1999/02/22-rdf-syntax-ns#");

        // Set the necessary values within this XML specification
        XmlNodeList ndxDescription = pdxModel.SelectNodes("./descendant::rdf:Description", nsModel);
        XmlNode ndxThis = null;
        XmlNode ndxWork = null;

        // The first <Description> node specifies the string we are looking for
        ndxThis = ndxDescription.Item(0);
        // Set the start and end 
        ndxThis.Attributes["rdf:about"].Value = "http://www.aksw.org/gerbil/NifWebService/request_0#char=" + 
          iStart + "," + iEnd;
        // Set the begin and end index
        ndxWork = ndxThis.SelectSingleNode("./child::nif:beginIndex", nsModel);
        ndxWork.InnerText = iStart.ToString();
        ndxWork = ndxThis.SelectSingleNode("./child::nif:endIndex", nsModel);
        ndxWork.InnerText = iEnd.ToString();
        // Set the item string
        ndxWork = ndxThis.SelectSingleNode("./child::nif:anchorOf", nsModel);
        ndxWork.InnerText = sItem;
        // Set the referencecontext
        ndxWork = ndxThis.SelectSingleNode("./child::nif:referenceContext", nsModel);
        ndxWork.Attributes["rdf:resource"].Value = "http://www.aksw.org/gerbil/NifWebService/request_0#char=0," + sContext.Length;

        // The second <Description> node specifies the context
        ndxThis = ndxDescription.Item(1);
        // Set the start and end 
        ndxThis.Attributes["rdf:about"].Value = "http://www.aksw.org/gerbil/NifWebService/request_0#char=0," + sContext.Length;
        // Set the context string
        ndxWork = ndxThis.SelectSingleNode("./child::nif:isString", nsModel);
        ndxWork.InnerText = sContext;
        // Set the end index
        ndxWork = ndxThis.SelectSingleNode("./child::nif:endIndex", nsModel);
        ndxWork.InnerText = sContext.Length.ToString();

        // Load and read the xml text
        RdfXmlParser rdfParser = new RdfXmlParser();

        String sPdx = pdxModel.SelectSingleNode("./descendant-or-self::rdf:RDF", nsModel).OuterXml;

        rdfParser.Load(g, new StringReader(sPdx));



        //// Specify namespaces
        //g.NamespaceMap.AddNamespace("nif", new Uri("http://persistence.uni-leipzig.org/nlp2rdf/ontologies/nif-core#"));
        //g.NamespaceMap.AddNamespace("rdf", new Uri("http://www.w3.org/1999/02/22-rdf-syntax-ns#"));
        //// Specify Nodes
        //IUriNode gerbilContext = g.CreateUriNode(UriFactory.Create("http://www.aksw.org/gerbil/NifWebService/request_0#char=0,"+sContext.Length));
        //IUriNode nifIsString = g.CreateUriNode("nif:isString");
        //IUriNode rdfType = g.CreateUriNode("rdf:type");
        //IUriNode nifBeginIndex = g.CreateUriNode("nif:beginIndex");
        //IUriNode nifEndIndex = g.CreateUriNode("nif:endIndex");
        //IUriNode nifRefContext = g.CreateUriNode("nif:referenceContext");
        //IUriNode nifAnchor = g.CreateUriNode("nif:anchorOf");

        //// The context and the item should be turned into literals -- but then for DUTCH
        //ILiteralNode litContext = g.CreateLiteralNode(sContext, sLanguage);
        //ILiteralNode litItem = g.CreateLiteralNode(sItem, sLanguage);

        //// Define the context in RDF elements
        //g.Assert(new Triple(gerbilContext, nifIsString, litContext));

      } catch (Exception e) {
        errHandle.DoError("entity/turtle", e); // Provide standard error message
      }
    }

    /* -------------------------------------------------------------------------------------
     * Name:        create
     * Goal:        Convert the RDF contents into a Turtle request string
     * History:
     * 7/nov/2016 ERK Created
       ------------------------------------------------------------------------------------- */
    public String create() {
      String sBack = "";

      try {
        // Output the graph
        // TurtleWriter tWriter = new TurtleWriter();
        CompressingTurtleWriter tWriter = new CompressingTurtleWriter();
        sBack = VDS.RDF.Writing.StringWriter.Write(g, tWriter);
        return sBack;
      } catch (Exception e) {
        errHandle.DoError("entity/turtle/create", e); // Provide standard error message
        return "";
      }
    }

    private static Stream GenerateStreamFromString(string s) {
      MemoryStream stream = new MemoryStream();
      StreamWriter writer = new StreamWriter(stream);
      writer.Write(s);
      writer.Flush();
      stream.Position = 0;
      return stream;
    }
  }

}

