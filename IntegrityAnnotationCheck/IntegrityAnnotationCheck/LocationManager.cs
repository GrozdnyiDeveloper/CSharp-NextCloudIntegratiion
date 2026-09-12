using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using System;
using System.Collections.Generic;
using System.IdentityModel.Metadata;
using System.Linq;
using System.Net.PeerToPeer;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using System.Web.Services.Description;
using System.IO;

namespace IntegrityAnnotationCheck
{
    public class LocationManager
    {
        private IOrganizationService service;
        private string baseUrl;

        public LocationManager(IOrganizationService service, string baseUrl)
        {
            this.service = service;
            this.baseUrl = baseUrl;
        }

        // Рекурсивный метод постройки полного пути к созданной записи сущности
        // Он проходит по иерархии файловой структуры и проверяет у найденных записей наличие записи Хранилище документов
        // Если проверка не пройдена - создаёт для них запись Хранилище документов
        public List<(Guid Id, string FolderName)> BuildFolderPath(Entity entity, ref List<(Guid Id, string FolderName)> folderPath, ref string parentFolderNameAttribute, ref Entity project, ref bool iswholesale)
        {
            // Получаем родительскую запись по иерархии у entity
            Entity parent = SearchParentEntity(entity, ref parentFolderNameAttribute, ref project);

            string folderName;
            // Если родительская запись найдена
            if (parent != null)
            {
                // Получаем название папки
                folderName = parent.GetAttributeValue<string>(parentFolderNameAttribute);

                // Если текущая запись сущности по иерархии == entity - значит это Проектная компания, заканчиваем рекурсию
                if (parent.Id == entity.Id)
                {
                    return folderPath;
                }
            }
            else
            {
                // Останавливает рекурсию и возвращает пустую строку, чтобы отправить сообщение об ошибке
                folderPath = new List<(Guid Id, string FolderName)>();
                return folderPath;
            }

            // Добавляет полученное название папки к строке полного пути к файлу созданной сущности
            folderPath.Add((parent.Id, folderName));
            // Продолжаем идти по иерархии
            return BuildFolderPath(parent, ref folderPath, ref parentFolderNameAttribute, ref project, ref iswholesale);
        }

        // Ищем родительскую запись сущности и меняем строку название для папки у родительской записи сущности
        public Entity SearchParentEntity(Entity entity, ref string parentFolderNameAttribute, ref Entity project)
        {
            Entity parent = null;
            parentFolderNameAttribute = null;

            try
            {
                // Из-за того, что у разных сущностей разные названия полей и форматы названия папок, пришлось импровизировать через switch
                switch (entity.LogicalName)
                {
                    // Договор и Оптовая сделка
                    case "opportunity":
                        // Отличаем Договор и Оптовую сделку по значению new_iswholesale или tisa_typeofsale. Если true - Оптовая сделка, false - Договор
                        if ((entity.GetAttributeValue<OptionSetValue>("tisa_typeofsale").Value == 8 ||
                             entity.GetAttributeValue<OptionSetValue>("tisa_typeofsale").Value == 16)
                             ||
                             entity.GetAttributeValue<bool>("new_iswholesale"))
                        {
                            // Ищем родительскую запись Оптовой сделки - Классификатор
                            parent = service.Retrieve("tisa_classifier", entity.GetAttributeValue<EntityReference>("new_wholesale_classificatorid").Id, GetNecessaryAttributes("tisa_classifier"));
                            parentFolderNameAttribute = "tisa_spfolder";
                        }
                        else
                        {
                            // Ищем родительскую запись Договора - Объект недвижимости
                            parent = service.Retrieve("tisa_article", entity.GetAttributeValue<EntityReference>("tisa_articleid").Id, GetNecessaryAttributes("tisa_article"));
                            parentFolderNameAttribute = "tisa_code";
                        }
                        break;
                    // Обращение
                    case "incident":
                        // Ищем родительскую запись Обращения - Объект недвижимости
                        parent = service.Retrieve("tisa_article", entity.GetAttributeValue<EntityReference>("new_articleid").Id, GetNecessaryAttributes("tisa_article"));
                        parentFolderNameAttribute = "tisa_code";
                        break;
                    // Объект недвижимости
                    case "tisa_article":
                        // Ищем родительскую запись Объекта недвижимости - Адрес (строение)
                        parent = service.Retrieve("tisa_address", entity.GetAttributeValue<EntityReference>("tisa_addressid").Id, GetNecessaryAttributes("tisa_address"));
                        parentFolderNameAttribute = "tisa_spfolder";
                        break;
                    // Адрес (строение)
                    case "tisa_address":
                        // Ищем родительскую запись Адреса (строения) - Классификатор
                        parent = service.Retrieve("tisa_classifier", entity.GetAttributeValue<EntityReference>("tisa_classifierid").Id, GetNecessaryAttributes("tisa_classifier"));
                        parentFolderNameAttribute = "tisa_spfolder";
                        break;
                    // Проект и Проектная компания
                    case "tisa_classifier":
                        // Определяем идентификатор Классификатора
                        int classifierGroup = entity.GetAttributeValue<OptionSetValue>("tisa_classifiergroup").Value;
                        switch (classifierGroup)
                        {
                            // Если значение = 50 - это Проект
                            case 50:
                                parent = service.Retrieve("tisa_classifier", entity.GetAttributeValue<EntityReference>("tisa_parentid").Id, GetNecessaryAttributes("tisa_classifier"));
                                project = entity;
                                parentFolderNameAttribute = "tisa_spfolder";
                                break;
                            // Если значение = 70 - это Проектная компания
                            case 70:
                                parent = entity;
                                parentFolderNameAttribute = "tisa_spfolder";
                                break;
                            // В остальных случаях ничего не делаем, метод вернёт null
                            default:
                                break;
                        }
                        break;
                    default:
                        break;
                }

            }
            catch (NullReferenceException ex)
            {
                throw ex;
            }

            return parent;
        }

        // Получаем название папки для entity
        public string GetFolderName(Entity entity, ref bool iswholesale)
        {
            string folderName = null;

            switch (entity.LogicalName)
            {
                // Договор и Оптовая сделка
                case "opportunity":
                    // Отличаем Договор и Оптовую сделку по значению new_iswholesale или tisa_typeofsale. Если true - Оптовая сделка, false - Договор
                    if (entity.GetAttributeValue<OptionSetValue>("tisa_typeofsale").Value == 8 ||
                    entity.GetAttributeValue<OptionSetValue>("tisa_typeofsale").Value == 16 ||
                    entity.GetAttributeValue<bool>("new_iswholesale"))
                    {
                        iswholesale = true;
                    }
                    if (entity.GetAttributeValue<DateTime>("tisa_contractdate") == DateTime.MinValue)
                    {
                        DateTime createdon = entity.GetAttributeValue<DateTime>("createdon");
                        if (createdon == DateTime.MinValue)
                        {
                            createdon = DateTime.Now;
                        }
                        folderName = entity.GetAttributeValue<string>("name") + " " + createdon.ToString("dd.MM.yyyy");
                    }
                    else
                    {
                        folderName = entity.GetAttributeValue<string>("name") + " " + entity.GetAttributeValue<DateTime>("tisa_contractdate").AddHours(5).ToString("dd.MM.yyyy");
                    }
                    break;
                // Обращение
                case "incident":
                    folderName = "Обращения";
                    break;
                // Объект недвижимости
                case "tisa_article":
                    folderName = entity.GetAttributeValue<string>("tisa_code");
                    break;
                // Адрес (строение)
                case "tisa_address":
                    folderName = entity.GetAttributeValue<string>("tisa_spfolder");
                    break;
                // Проект и Проектная компания
                case "tisa_classifier":
                    folderName = entity.GetAttributeValue<string>("tisa_spfolder");
                    break;
                default:
                    break;
            }

            return folderName;
        }

        public Entity GetEntityFromAnnotation(Entity entity)
        {
            if (entity.LogicalName == "annotation")
            {
                return service.Retrieve(entity.GetAttributeValue<string>("objecttypecode"), entity.GetAttributeValue<EntityReference>("objectid").Id, GetNecessaryAttributes(entity.GetAttributeValue<string>("objecttypecode")));
            }
            else
            {
                return entity;
            }
        }

        // Возвращает ColumnSet только необходимых аттрибутов для запрашиваемой записи сущности, чтобы сэкономить ресурсы
        public ColumnSet GetNecessaryAttributes(string entityLogicalName)
        {
            ColumnSet attributes = null;

            switch (entityLogicalName)
            {
                // Договор
                case "opportunity":
                    attributes = new ColumnSet("tisa_typeofsale", "new_iswholesale", "new_wholesale_classificatorid", "tisa_articleid", "name", "createdon", "tisa_contractdate");
                    break;
                // Обращение
                case "incident":
                    attributes = new ColumnSet("new_articleid");
                    break;
                // Объект недвижимости
                case "tisa_article":
                    attributes = new ColumnSet("tisa_addressid", "tisa_code");
                    break;
                // Адрес (строение)
                case "tisa_address":
                    attributes = new ColumnSet("tisa_classifierid", "tisa_spfolder");
                    break;
                // Классификатор
                case "tisa_classifier":
                    attributes = new ColumnSet("tisa_classifiergroup", "tisa_parentid", "tisa_spfolder");
                    break;
                case "crmpark_storagelocation":
                    attributes = new ColumnSet("subject", "regardingobjectid", "crmpark_parentstoragelocationid");
                    break;
                default:
                    break;
            }

            return attributes;
        }

        public string GetFolderSafeName(string entityName)
        {
            StringBuilder nameBuilder = new StringBuilder();

            var illegalChars = Path.GetInvalidPathChars().Union(new char[] { '#', '%', ':', '?', '*', '{', '}', '<', '>', '/', '\\', '|', '&', '«', '»', '+', '~' });
            foreach (char c in entityName)
            {
                if (!illegalChars.Contains(c))
                {
                    nameBuilder.Append(c);
                }
                else
                {
                    nameBuilder.Append(" ");
                }
            }

            return nameBuilder.ToString().Trim();
        }

        public string BuildFolderPathFromList(List<string> folderPath)
        {
            string result = "";

            foreach (string folderName in folderPath)
            {
                result = $"{folderName}/{result}";
            }

            return result;
        }

        public string BuildSafeFolderPathFromList(List<(Guid Index, string FolderName)> folderPath)
        {
            string result = "";

            if (folderPath.Count > 0)
            {
                foreach ((Guid Id, string FolderName) tuple in folderPath)
                {
                    result = $"{GetFolderSafeName(tuple.FolderName)}/{result}";
                }
            }

            return result;
        }
    }
}
