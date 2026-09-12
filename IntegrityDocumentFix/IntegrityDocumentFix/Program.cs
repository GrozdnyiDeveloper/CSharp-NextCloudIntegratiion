using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Tooling.Connector;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Controls;

namespace IntegrityDocumentFix
{
    public class Program
    {
        static CrmServiceClient crmServiceClient;
        static WebDavManager web;
        static StreamWriter log;
        static int foundFilesCount = 0, successfulFilesCount = 0, failedFilesCount = 0, correctFilesCount = 0;
        static void Main(string[] args)
        {
            List<string> filePaths = new List<string>();
            
            try
            {
                crmServiceClient = new CrmServiceClient(System.Configuration.ConfigurationManager.ConnectionStrings["CRM"].ConnectionString);
                web = new WebDavManager(crmServiceClient);

                log = new StreamWriter("Log.txt", true);
                log.Write("\n\n");
                Write("Начало работы приложения");
                Write("Поиск файлов...");
                // Получаем пути всех файлов из NextCloud, которые были изменены за период с 30 по 31 января 2025 г.
                filePaths = GetAllFiles();

                foundFilesCount = filePaths.Count;
                Write("Завершение поиска файлов. Всего найдено - " + foundFilesCount); // 3768
            }
            catch (Exception ex)
            {
                Write("ERROR: " + ex.Message);
                log.Close();
                Console.ReadLine();
                return;
            }

            Write("----------------------------------------");

            // Для каждого файла
            foreach (string filePath in filePaths)
            {
                try
                {
                    Write($"Получаем данные из файла по пути {filePath}");
                    // Получаем данные самого файла
                    var fileBodyBytes = web.GetfileInfo(filePath);

                    Write("Проверяем на корректность данные файла");
                    // Проверяем полученные данные на корректность
                    CheckIncorrectIncoding(fileBodyBytes);
                    Write("Проверка пройдена успешно, данные необходимо конвертировать в корректный формат");

                    Write("Конвертируем полученные данные из файла");
                    // Конвертируем их из некорректного формата в обычный и обратно уже в корректный
                    string fileBodyString = Encoding.Default.GetString(fileBodyBytes);
                    var fileBody = Convert.FromBase64String(fileBodyString);

                    Write($"Переписываем данные файла на сервере по пути {filePath}");
                    // Переписываем файл на сервере под корректный формат 
                    web.ReplaceFile(filePath, fileBody);

                    successfulFilesCount++;
                    Write($"Успешно обработан файл по пути {filePath}", "\n");
                }
                catch (Exception ex)
                {
                    // Выводим сообщение об корректности или ошибке
                    if (ex.Message == "CORRECT")
                    {
                        correctFilesCount++;
                        Write($"{ex.Message}: Формат данных файла по пути {filePath} корректен и не нуждается в конвертации", "\n");
                    }
                    else
                    {
                        failedFilesCount++;
                        Write($"ERROR: Произошла ошибка при обработке файла по пути {filePath}. Ошибка: {ex.Message}", "\n");
                    }
                }
            }

            Write("----------------------------------------");

            Write($"Завершение работы приложения.", "\n" +
                $"Всего обработано файлов - {foundFilesCount}. \n" +
                $"Успешно перекодировано - {successfulFilesCount}. \n" +
                $"Не требует перекодирования - {correctFilesCount}. \n" +
                $"Ошибки - {failedFilesCount}.");
            log.Close();
            Console.ReadLine();
        }

        static List<String> GetAllFiles() 
        {
            var result = new List<String>();

            try
            {
                // Получаем все папки
                var folders = web.GetFolders();

                // Для каждой папки
                foreach (var folder in folders)
                {
                    try
                    {
                        // Получаем все файлы по фильтру из папки и добавляе в результат
                        var files = web.FindFiles(folder);
                        result.AddRange(files);
                    }
                    catch (Exception ex)
                    {
                        throw new Exception("Произошла ошибка при получении файлов из директории " + folder + ". Ошибка: " + ex.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                throw new Exception("Ошибка при обработке папок: " + ex.Message);
            }

            return result;
        }

        static void CheckIncorrectIncoding(byte[] fileBodyBytes)
        {
            try
            {
                // Конвертируем байты в строку и проверяем валидность Base64
                string base64String = Encoding.ASCII.GetString(fileBodyBytes);
                byte[] decodedBytes = Convert.FromBase64String(base64String);
            }
            catch
            {
                throw new Exception("CORRECT");
            }
        }

        static void Write(string message, string afterMessage = "")
        {
            Console.WriteLine($"{message} | {DateTime.Now}{afterMessage}");
            log.WriteLine($"{message} | {DateTime.Now}{afterMessage}");
        }
    }
}
