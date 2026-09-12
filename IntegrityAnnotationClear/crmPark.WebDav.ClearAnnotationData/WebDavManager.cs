using Microsoft.Xrm.Sdk.Query;
using Microsoft.Xrm.Sdk;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml;

namespace crmPark.WebDav.ClearAnnotationData
{
    public class WebDavManager
    {
        private IOrganizationService service;

        private Entity baseUrlRecord;
        private string baseUrl;
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
            }
            else
            {
                baseUrl = null;
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

        /// <summary>
        /// Проверка существования файла
        /// </summary>
        /// <param name="requestUrl"></param>
        /// <returns></returns>
        public bool CheckFileExist(string requestUrl)
        {
            requestUrl = $"{baseUrl}/{requestUrl}";
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(requestUrl);
            request.Credentials = new NetworkCredential(username, password);
            request.Method = "HEAD";
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            try
            {
                using (WebResponse response = request.GetResponse())
                {
                    // Файл с таким именем уже существует
                    return true;
                }
            }
            catch (WebException)
            {
                // Файл с таким именем не существует
                return false;
            }
        }

        // Метод загрузки файла в Файловом хранилище
        public void UploadFileInWebDav(string folderPath, string fileName, byte[] fileBody, ref bool isUploadComplete)
        {
            // Проверяем наличие папок и если что, создаём их
            CreateFolderInWebDav(folderPath);
            //счетчик попыток создания
            int i = 0;

            while (i < _SET_INDEX_LIMIT && !isUploadComplete)
            {
                string requestUrl = SetFileIndex(folderPath, fileName);
                WriteFile(requestUrl, fileBody, ref isUploadComplete);
                i++;
            }
        }

        // Создание папки в Файловом хранилище
        public void CreateFolderInWebDav(string folderPath)
        {
            // Разделяем полный путь на строки папок, чтобы проверить наличие каждой в Файловом хранилище
            string[] folderNames = folderPath.Split('/');
            string currentFolderPath = folderNames[0];
            if (baseUrl != null)
            {
                for (int i = 0; i < folderNames.Length; i++)
                {
                    if (i != 0)
                    {
                        currentFolderPath = $"{currentFolderPath}/{folderNames[i]}";
                        if (string.IsNullOrWhiteSpace(folderNames[i]))
                        {
                            break;
                        }
                    }

                    bool isFolderExist = CheckFolderExist(currentFolderPath);
                    if (!isFolderExist)
                    {
                        // Запрос на создание папки
                        string requestUrl = $"{baseUrl}/{currentFolderPath}";
                        HttpWebRequest request = (HttpWebRequest)WebRequest.Create(requestUrl);
                        request.Credentials = new NetworkCredential(username, password);
                        ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                        request.Method = "MKCOL";
                        try
                        {
                            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                            {
                            }
                        }
                        catch (WebException ex)
                        {
                            break;
                        }
                    }
                }
            }
        }

        // Проверка наличия папки в Файловом хранилище
        public bool CheckFolderExist(string folderPath)
        {
            string requestUrl = $"{baseUrl}/{folderPath}";
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(requestUrl);
            request.Credentials = new NetworkCredential(username, password);
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            request.Method = "PROPFIND";
            request.ContentType = "text/xml";
            request.Headers.Add("Brief", "f");
            try
            {
                using (WebResponse response = request.GetResponse())
                {
                    // Получаем статусный код ответа
                    HttpWebResponse httpResponse = (HttpWebResponse)response;
                    HttpStatusCode statusCode = httpResponse.StatusCode;

                    // Если статусный код равен 200 (ОК)
                    if (statusCode == HttpStatusCode.OK)
                    {
                        return true;
                    }
                }
            }
            catch (WebException ex)
            {
                // Если статусный код равен 404 (не найдено)
                if (ex.Response != null && ((HttpWebResponse)ex.Response).StatusCode == HttpStatusCode.NotFound)
                {
                    return false;
                }
            }

            // Возвращает true, чтобы не создавать папку в Файловом хранилище по причине неожиданного результата запроса WebDav
            return true;
        }

        /// <summary>
        ///  метод для установки индекса файла в случае, если файл с похожим названием уже существует в Файловом хранилище
        /// </summary>
        public string SetFileIndex(string folderPath, string fileName)
        {
            string fileNameNoExstension = Path.GetFileNameWithoutExtension(fileName);
            string fileExtension = Path.GetExtension(fileName);

            try
            {
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create($"{baseUrl}/{folderPath}/");
                request.Method = "PROPFIND";
                request.Headers.Add("Depth", "1"); // Depth: 1 to get all items in the directory
                request.Credentials = new NetworkCredential(username, password);
                request.ContentType = "application/xml";
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;

                // Send the request
                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                {
                    using (Stream responseStream = response.GetResponseStream())
                    {
                        if (responseStream != null)
                        {
                            using (StreamReader reader = new StreamReader(responseStream))
                            {
                                string responseContent = reader.ReadToEnd();


                                return $"{baseUrl}/{folderPath}/{GetHighestIndex(responseContent, fileNameNoExstension, fileExtension)}";
                            }
                        }
                        else
                        {
                            //Нет ответа - без индекса
                            return $"{baseUrl}/{folderPath}/{fileNameNoExstension}{fileExtension}";
                        }
                    }
                }
            }
            catch (WebException ex)
            {//Поймали ошибку - пытаемся задать без индекса 
                return $"{baseUrl}/{folderPath}/{fileNameNoExstension}{fileExtension}";
            }
        }

        void WriteFile(string requestUrl, byte[] fileBody, ref bool isUploadComplete)
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(requestUrl);
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
                    isUploadComplete = true;
                }
            }
            catch (WebException ex)
            {
                isUploadComplete = false;
            }
        }

        /// <summary>
        /// Получение информации о
        /// </summary>
        /// <param name="xmlResponse"></param>
        /// <param name="fileNameToSearch"></param>
        static string GetHighestIndex(string xmlResponse, string fileNameToSearch, string fileExtension)
        {
            XmlDocument doc = new XmlDocument();
            doc.LoadXml(xmlResponse);

            XmlNamespaceManager namespaceManager = new XmlNamespaceManager(doc.NameTable);
            namespaceManager.AddNamespace("d", "DAV:");

            XmlNodeList nodes = doc.SelectNodes("//d:response", namespaceManager);
            List<string> fileNames = new List<string>();
            foreach (XmlNode node in nodes)
            {
                XmlNode hrefNode = node.SelectSingleNode("d:href", namespaceManager);
                if (hrefNode != null)
                {
                    //последняя часть пути (название файла)
                    string encodedFileName = hrefNode.InnerText.Substring(hrefNode.InnerText.LastIndexOf('/') + 1);

                    // декодирование файла
                    string decodedFileName = Uri.UnescapeDataString(encodedFileName);
                    if (!String.IsNullOrEmpty(decodedFileName))
                    {
                        fileNames.Add(decodedFileName);
                    }

                }
            }
            var filesWithSameName = fileNames.Where(x => x.IndexOf(fileNameToSearch) != -1).ToList();
            if (filesWithSameName.Any())
            {
                if (filesWithSameName.Count == 1)
                {
                    return $"{fileNameToSearch}(1){fileExtension}";
                }
                int highestNumber = GetHighestNumber(filesWithSameName);
                return $"{fileNameToSearch}({highestNumber + 1}){fileExtension}";
            }
            else
            {
                return $"{fileNameToSearch}{fileExtension}";
            }


        }

        /// <summary>
        /// Получение наивысшего значения индекса из списка файлов
        /// </summary>
        /// <param name="fileNames"></param>
        /// <returns></returns>
        static int GetHighestNumber(List<string> fileNames)
        {
            int maxNumber = 0;
            Regex regex = new Regex(@"\((\d+)\)");

            foreach (string fileName in fileNames)
            {
                Match match = regex.Match(fileName);
                if (match.Success && int.TryParse(match.Groups[1].Value, out int number))
                {
                    maxNumber = Math.Max(maxNumber, number);
                }
            }

            return maxNumber;
        }
    }
}
