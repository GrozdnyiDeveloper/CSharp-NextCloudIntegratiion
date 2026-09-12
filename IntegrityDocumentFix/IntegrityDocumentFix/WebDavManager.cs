using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using System.Web.UI.WebControls;
using System.Windows.Documents;
using System.Xml;
using System.Xml.Linq;
using static IntegrityDocumentFix.Program;

namespace IntegrityDocumentFix
{
    public class WebDavManager
    {
        private IOrganizationService service;

        private Entity baseUrlRecord;
        private string baseUrl;
        private string baseUrlHead;
        private string username;
        private string password;
        //Количество доступных попыток для переименования файла 
        private const int _SET_INDEX_LIMIT = 10;

        public WebDavManager(IOrganizationService service)
        {
            this.service = service;

            baseUrlRecord = GetWebDavBaseUrlRecord();
            if (baseUrlRecord != null)
            {
                baseUrl = baseUrlRecord.GetAttributeValue<string>("subject");
                Uri uri = new Uri(baseUrl);
                baseUrlHead = $"{uri.Scheme}://{uri.Host}";
            }
            else
            {
                baseUrl = null;
                baseUrlHead = null;
            }

            username = GetBNGSettingRecordKey("WebDavLogin");
            password = GetBNGSettingRecordKey("WebDavPassword");
        }

        // Выполняет поиск корневой записи Хранилища документов для получения базового адреса Файлового хранилища
        public Entity GetWebDavBaseUrlRecord()
        {
            QueryExpression query = new QueryExpression("crmpark_storagelocation");
            query.ColumnSet = new ColumnSet("subject");
            query.Criteria.AddCondition("crmpark_parentstoragelocationid", ConditionOperator.Null);

            EntityCollection results = service.RetrieveMultiple(query);

            if (results.Entities.Count > 0)
            {
                return results.Entities[0];
            }
            else
            {
                return null;
            }
        }

        // Получение значение поля Ключ из записи Параметра для подключения к Файловому хранилищу
        public string GetBNGSettingRecordKey(string bngName)
        {
            QueryExpression query = new QueryExpression("bng_setting");
            query.ColumnSet = new ColumnSet("bng_key");
            query.Criteria.AddCondition("bng_name", ConditionOperator.Equal, bngName);

            EntityCollection results = service.RetrieveMultiple(query);

            if (results.Entities.Count > 0)
            {
                Entity bngSetting = results.Entities[0];
                string result = bngSetting.GetAttributeValue<string>("bng_key");

                return result;
            }
            else
            {
                return null;
            }
        }

        public string GetCurrentMainUrl()
        {
            return baseUrl;
        }

        public List<string> GetFolders()
        {
            var result = new List<String>();
            
            var request = (HttpWebRequest)WebRequest.Create($"{baseUrl}");
            request.Method = "PROPFIND";
            request.Credentials = new NetworkCredential(username, password);
            request.ContentType = "application/xml";
            request.Headers.Add("Depth", "1");
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;

            string xmlBody = "<d:propfind xmlns:d='DAV:'><d:prop><d:displayname/><d:resourcetype/><d:getlastmodified/></d:prop></d:propfind>";

            byte[] byteArray = Encoding.UTF8.GetBytes(xmlBody);
            request.ContentLength = byteArray.Length;

            // Записываем тело запроса
            using (var dataStream = request.GetRequestStream())
            {
                dataStream.Write(byteArray, 0, byteArray.Length);
            }

            // Получаем ответ
            using (var response = (HttpWebResponse)request.GetResponse())
            using (var responseStream = response.GetResponseStream())
            using (var reader = new StreamReader(responseStream))
            {
                string responseContent = reader.ReadToEnd();

                // Парсим XML ответ
                var xmlDoc = new XmlDocument();
                xmlDoc.LoadXml(responseContent);

                // Находим все элементы response
                var namespaceManager = new XmlNamespaceManager(xmlDoc.NameTable);
                namespaceManager.AddNamespace("d", "DAV:");

                var responseNodes = xmlDoc.SelectNodes("//d:response", namespaceManager);

                foreach (XmlNode responseNode in responseNodes)
                {
                    // Получаем путь к ресурсу
                    var hrefNode = responseNode.SelectSingleNode("d:href", namespaceManager);
                    if (hrefNode == null)
                        continue;

                    // Пропускаем корневую директорию
                    string filePath = Uri.UnescapeDataString(hrefNode.InnerText.EndsWith("/") ? hrefNode.InnerText.Substring(0, hrefNode.InnerText.Length - 1) : hrefNode.InnerText);
                    if ($"{baseUrl}" == $"{baseUrlHead}{filePath}")
                        continue;

                    // Пропускаем файлы
                    var isFolder = responseNode.SelectSingleNode("d:propstat/d:prop/d:resourcetype/d:collection", namespaceManager);
                    if (isFolder == null)
                        continue;

                    // Получаем название и добавляем в результат
                    var name = responseNode.SelectSingleNode("d:propstat/d:prop/d:displayname", namespaceManager).InnerText;
                    result.Add($"/{name}");
                }
            }

            return result;
        }

        public List<string> FindFiles(string folderPath = null)
        {
            string[] separators = { "dav" };
            string[] baseUrlParts = baseUrl.Split(separators, StringSplitOptions.None);
            baseUrlParts[0] += "dav";

            List<string> result = new List<string>();

            var request = (HttpWebRequest)WebRequest.Create(baseUrlParts[0]);
            request.Credentials = new NetworkCredential(username, password);
            request.Method = "SEARCH";
            request.ContentType = "application/xml";
            request.Headers.Add("Depth", "infinity");
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;

            // Период для фильтрации
            DateTime startDate = new DateTime(2025, 1, 30);
            DateTime endDate = new DateTime(2025, 1, 31).AddDays(1).AddTicks(-1); // До конца дня 31.01

            string xmlBody = $@"<d:searchrequest xmlns:d=""DAV:"">
                <d:basicsearch>
                    <d:select>
                        <d:prop>
                            <d:displayname/>
                            <d:resourcetype/>
                            <d:getlastmodified/>
                        </d:prop>
                    </d:select>
                    <d:from>
                        <d:scope>
                            <d:href>{baseUrlParts[1] + folderPath}</d:href>
                            <d:depth>infinity</d:depth>
                        </d:scope>
                    </d:from>
                    <d:where>
                        <d:and>
                            <d:gt>
                                <d:prop>
                                    <d:getlastmodified/>
                                </d:prop>
                                <d:literal>{startDate.ToString("yyyy-MM-ddTHH:mm:ssZ")}</d:literal>
                            </d:gt>
                            <d:lt>
                                <d:prop>
                                    <d:getlastmodified/>
                                </d:prop>
                                <d:literal>{endDate.ToString("yyyy-MM-ddTHH:mm:ssZ")}</d:literal>
                            </d:lt>
                        </d:and>
                    </d:where>
                    <d:orderby/>
                </d:basicsearch>
            </d:searchrequest>";

            byte[] byteArray = Encoding.UTF8.GetBytes(xmlBody);
            request.ContentLength = byteArray.Length;

            // Записываем тело запроса
            using (var dataStream = request.GetRequestStream())
            {
                dataStream.Write(byteArray, 0, byteArray.Length);
            }

            // Получаем ответ
            using (var response = (HttpWebResponse)request.GetResponse())
            using (var responseStream = response.GetResponseStream())
            using (var reader = new StreamReader(responseStream))
            {
                string responseContent = reader.ReadToEnd();

                // Парсим XML ответ
                var xmlDoc = new XmlDocument();
                xmlDoc.LoadXml(responseContent);

                // Находим все элементы response
                var namespaceManager = new XmlNamespaceManager(xmlDoc.NameTable);
                namespaceManager.AddNamespace("d", "DAV:");

                var responseNodes = xmlDoc.SelectNodes("//d:response", namespaceManager);

                foreach (XmlNode responseNode in responseNodes)
                {
                    // Получаем путь к ресурсу
                    var hrefNode = responseNode.SelectSingleNode("d:href", namespaceManager);
                    if (hrefNode == null)
                        continue;

                    // Пропускаем корневую директорию
                    string filePath = Uri.UnescapeDataString(hrefNode.InnerText.EndsWith("/") ? hrefNode.InnerText.Substring(0, hrefNode.InnerText.Length - 1) : hrefNode.InnerText);
                    if ($"{baseUrl}{folderPath}" == $"{baseUrlHead}{filePath}")
                        continue;

                    // Пропускаем папки
                    var isFolder = responseNode.SelectSingleNode("d:propstat/d:prop/d:resourcetype/d:collection", namespaceManager);
                    if (isFolder != null) 
                        continue;

                    // Добавляем путь до файла в результат
                    result.Add(baseUrlHead + filePath);
                }
            }

            return result;
        }

        public byte[] GetfileInfo(string filePath)
        {
            byte[] fileBodyBytes = null;

            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create($"{filePath}");
            request.Credentials = new NetworkCredential(username, password);
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            request.Method = "GET";
            request.ContentType = "application/octet-stream";

            try
            {
                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                {
                    if (response.StatusCode != HttpStatusCode.OK)
                    {
                        throw new WebException($"HTTP ошибка: {response.StatusCode}");
                    }

                    // Получаем поток и сохраняем в файл
                    using (Stream responseStream = response.GetResponseStream())
                    {
                        using (MemoryStream memoryStream = new MemoryStream())
                        {
                            responseStream.CopyTo(memoryStream);
                            fileBodyBytes = memoryStream.ToArray();
                        }
                    }
                }
            }
            catch (WebException ex)
            {
                Console.WriteLine($"Ошибка: {ex.Message}");
            }

            return fileBodyBytes;
        }

        public void ReplaceFile(string filePath, byte[] fileBody) 
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create($"{filePath}");
            request.Credentials = new NetworkCredential(username, password);
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            request.Method = "PUT";
            request.ContentType = "application/octet-stream";
            request.ContentLength = fileBody.Length;

            try
            {
                // Пишем данные в поток запроса
                using (Stream requestStream = request.GetRequestStream())
                {
                    requestStream.Write(fileBody, 0, fileBody.Length);
                }

                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                {
                    if (response.StatusCode != HttpStatusCode.OK)
                    {
                        //throw new WebException($"HTTP ошибка: {response.StatusCode}");
                    }
                }
            }
            catch (WebException ex)
            {
                Console.WriteLine($"Ошибка: {ex.Message}");
            }
        }
    }
}
