using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ATool
{
    class Program
    {
        // 암호화와 동일한 키, IV 사용 필수
        static readonly byte[] aesKey = Encoding.UTF8.GetBytes("0123456789abcdef0123456789abcdef"); // 32 bytes
        static readonly byte[] aesIV = Encoding.UTF8.GetBytes("abcdef9876543210"); // 16 bytes

        static readonly HttpClient client = new HttpClient();

        static string agentId = LoadAgentId();

        static string LoadAgentId()
        {
            string appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData); // 저장 루트 : C:\Users\YJ\AppData\Roaming
            string filePath = Path.Combine(appDataPath, "AgentID.id");

            if (File.Exists(filePath))
            {
                return File.ReadAllText(filePath).Trim();
            }
            else
            {
                return "UnknownAgent";
            }
        }


        static void Main(string[] args)
        {
            if (args.Length == 0)
            {
                return;
            }

            string encryptedFilePath = args[0];

            if (!File.Exists(encryptedFilePath))
            {
                return;
            }

            if (!encryptedFilePath.EndsWith(".enc", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            ProcessEncryptedFile(encryptedFilePath);
        }

        static void ProcessEncryptedFile(string encryptedFilePath)
        {
            string directory = Path.GetDirectoryName(encryptedFilePath);
            string fileName = Path.GetFileName(encryptedFilePath);
            string originalFileName = fileName.Substring(0, fileName.Length - 4); // .enc 제거
            string originalFilePath = Path.Combine(directory, originalFileName);
            string backupFilePath = Path.Combine(directory, "~" + fileName + ".bak");

            if (File.Exists(originalFilePath) || File.Exists(backupFilePath))
            {
                return;
            }

            try
            {
                File.Move(encryptedFilePath, backupFilePath);
                DecryptFile(backupFilePath, originalFilePath);
                File.SetAttributes(originalFilePath, FileAttributes.Normal); // 읽기 전용 방지

                // 파일 시작 시간 기록
                DateTime originalLastWrite = File.GetLastWriteTime(originalFilePath);

                Process process = StartAssociatedProgram(originalFilePath);

                if (process != null)
                {
                    Task.Run(() => MonitorAndReEncrypt(process, originalFilePath, backupFilePath, encryptedFilePath, originalLastWrite));
                }
                else
                {
                    // 프로그램 실행 실패 시 파일 변경 모니터링으로 대체
                    Log("파일 변경 모니터링 대체", "프로그램 실행 실패", fileName);
                    Task.Run(() => MonitorFileChanges(originalFilePath, backupFilePath, encryptedFilePath, originalLastWrite));
                }

                Log("파일 복호화", "성공", fileName);

                // 메인 스레드에서 사용자 입력 대기 (강제 종료 방지)
                Console.CancelKeyPress += (sender, e) => {
                    e.Cancel = true;
                    Log("파일 재암호화", "성공", fileName);
                    CleanupAndReEncrypt(originalFilePath, backupFilePath, encryptedFilePath);
                    Environment.Exit(0);
                };

                // 무한 대기 (백그라운드 작업이 완료될 때까지)
                while (File.Exists(originalFilePath))
                {
                    Thread.Sleep(5000);
                }
            }
            catch (Exception ex)
            {
                Log("처리오류", "실패", ex.Message);

                if (File.Exists(backupFilePath) && !File.Exists(encryptedFilePath))
                {
                    File.Move(backupFilePath, encryptedFilePath);
                }
            }
        }

        static void DecryptFile(string encryptedFilePath, string decryptedFilePath)
        {
            string extension = Path.GetExtension(decryptedFilePath).ToLower();

            using (FileStream input = new FileStream(encryptedFilePath, FileMode.Open, FileAccess.Read))
            using (FileStream output = new FileStream(decryptedFilePath, FileMode.Create, FileAccess.Write))
            {
                try
                {
                    using (Aes aes = Aes.Create())
                    {
                        aes.Key = aesKey;
                        aes.IV = aesIV;

                        using (CryptoStream cryptoStream = new CryptoStream(input, aes.CreateDecryptor(), CryptoStreamMode.Read))
                        {
                            if (extension == ".txt")
                            {
                                using (var reader = new StreamReader(cryptoStream, Encoding.UTF8))
                                using (var writer = new StreamWriter(output, Encoding.UTF8))
                                {
                                    writer.Write(reader.ReadToEnd());
                                }
                            }
                            else
                            {
                                cryptoStream.CopyTo(output);
                            }
                        }
                    }
                }
                catch (CryptographicException)
                {
                    input.Position = 0;
                    output.Position = 0;
                    input.CopyTo(output);
                }
            }
        }

        static void EncryptFile(string originalFilePath, string encryptedFilePath)
        {
            using (FileStream input = new FileStream(originalFilePath, FileMode.Open, FileAccess.Read))
            using (FileStream output = new FileStream(encryptedFilePath, FileMode.Create, FileAccess.Write))
            {
                using (Aes aes = Aes.Create())
                {
                    aes.Key = aesKey;
                    aes.IV = aesIV;

                    using (CryptoStream cryptoStream = new CryptoStream(output, aes.CreateEncryptor(), CryptoStreamMode.Write))
                    {
                        input.CopyTo(cryptoStream);
                    }
                }
            }
        }

        static Process StartAssociatedProgram(string filePath)
        {
            try
            {
                var info = new ProcessStartInfo(filePath)
                {
                    UseShellExecute = true
                };
                Process process = Process.Start(info);

                // 프로세스가 실제로 시작되었는지 확인
                if (process != null && !process.HasExited)
                {
                    return process;
                }
                return null;
            }
            catch (Exception ex)
            {
                return null;
            }
        }

        static void MonitorAndReEncrypt(Process process, string originalFilePath, string backupFilePath, string encryptedFilePath, DateTime originalLastWrite)
        {
            try
            {

                // 프로세스 종료 대기
                process.WaitForExit();

                // 추가 대기 시간
                Thread.Sleep(3000);

                // 파일 해제 대기 (타임아웃 증가)
                if (WaitForFileRelease(originalFilePath, TimeSpan.FromMinutes(5)))
                {
                    CleanupAndReEncrypt(originalFilePath, backupFilePath, encryptedFilePath);
                }
                else
                {
                    Log("파일 변경 모니터링 대체", "파일 실행 중", originalFilePath);
                    MonitorFileChanges(originalFilePath, backupFilePath, encryptedFilePath, originalLastWrite);
                }
            }
            catch (Exception ex)
            {
                Log("프로세스 모니터링 오류", "실패", ex.Message);
                // 오류 발생 시 파일 변경 모니터링으로 대체
                MonitorFileChanges(originalFilePath, backupFilePath, encryptedFilePath, originalLastWrite);
            }
        }

        static void MonitorFileChanges(string originalFilePath, string backupFilePath, string encryptedFilePath, DateTime originalLastWrite)
        {

            DateTime lastCheckTime = DateTime.Now;
            bool fileWasModified = false;

            while (File.Exists(originalFilePath))
            {
                try
                {
                    DateTime currentLastWrite = File.GetLastWriteTime(originalFilePath);

                    // 파일이 수정되었는지 확인
                    if (currentLastWrite > originalLastWrite.AddSeconds(1))
                    {
                        fileWasModified = true;
                        originalLastWrite = currentLastWrite;
                        lastCheckTime = DateTime.Now;
                    }

                    // 파일이 수정된 후 일정 시간 동안 변경이 없으면 재암호화 시작
                    if (fileWasModified && (DateTime.Now - lastCheckTime).TotalMinutes > 2)
                    {
                        if (WaitForFileRelease(originalFilePath, TimeSpan.FromMinutes(3)))
                        {
                            CleanupAndReEncrypt(originalFilePath, backupFilePath, encryptedFilePath);
                            break;
                        }
                        else
                        {
                            lastCheckTime = DateTime.Now; // 다시 대기
                        }
                    }

                    Thread.Sleep(5000); // 5초마다 확인
                }
                catch (Exception ex)
                {
                    Log("파일 모니터링 오류", "실패", ex.Message);
                    Thread.Sleep(10000);
                }
            }
        }

        static bool WaitForFileRelease(string filePath, TimeSpan timeout)
        {
            DateTime start = DateTime.Now;
            int attempts = 0;

            while (DateTime.Now - start < timeout)
            {
                try
                {
                    using (FileStream fs = new FileStream(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    {
                        return true;
                    }
                }
                catch
                {
                    attempts++;
                    Thread.Sleep(3000); // 3초 간격으로 확인
                }
            }

            return false;
        }

        static void CleanupAndReEncrypt(string originalFilePath, string backupFilePath, string encryptedFilePath)
        {
            try
            {
                // 재암호화 시도
                int maxRetries = 3;
                bool encryptSuccess = false;

                for (int i = 0; i < maxRetries; i++)
                {
                    try
                    {
                        EncryptFile(originalFilePath, encryptedFilePath);
                        encryptSuccess = true;
                        Log("파일 재암호화 완료", "성공", originalFilePath);
                        break;
                    }
                    catch (Exception ex)
                    {
                        Log($"파일 재암호화 실패 (시도 {i + 1})", "대기", ex.Message);
                        if (i < maxRetries - 1)
                        {
                            Thread.Sleep(2000);
                        }
                    }
                }

                if (!encryptSuccess)
                {
                    Log("파일 재암호화 실패", "실패", originalFilePath);
                    if (File.Exists(backupFilePath))
                    {
                        File.Move(backupFilePath, encryptedFilePath);
                        Log("파일 재암호화 실패로 인한 백업 파일 복원", "성공", originalFilePath);
                    }
                    return;
                }

                // 원본 파일 삭제
                for (int i = 0; i < 10; i++)
                {
                    try
                    {
                        // 파일 속성 제거 후 삭제
                        Log("원본 파일 삭제", "성공", originalFilePath);
                        File.SetAttributes(originalFilePath, FileAttributes.Normal);
                        File.Delete(originalFilePath);
                        
                        break;
                    }
                    catch (Exception ex)
                    {
                        Log($"원본 파일 삭제 실패 (시도 {i + 1})", "대기", ex.Message);
                        Thread.Sleep(2000);
                    }
                }

                if (File.Exists(originalFilePath))
                {
                    Log("원본 파일 삭제", "실패", originalFilePath);
                }

                // 백업 파일 삭제
                if (File.Exists(backupFilePath))
                {
                    try
                    {
                        Log("백업 파일 삭제", "성공", backupFilePath);
                        File.Delete(backupFilePath);
                    }
                    catch (Exception ex)
                    {
                        Log("백업 파일 삭제 실패", "성공", ex.Message);
                    }
                }
            }
            catch (Exception ex)
            {

                // 최후의 복구 시도
                try
                {
                    if (File.Exists(backupFilePath) && !File.Exists(encryptedFilePath))
                    {
                        File.Move(backupFilePath, encryptedFilePath);
                        Log("백업 파일 복원", "성공", backupFilePath);
                    }
                }
                catch (Exception restoreEx)
                {
                    Log("백업 파일 복원", "실패", restoreEx.Message);
                }
            }
        }

        static void Log(string type, string result, string infor)
        {
            string time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            string val = $"{agentId}|{type}|{time}|{result}|{infor}";
            string url = $"http://192.168.0.82/Smsproject/Api/report.html?val=" + Uri.EscapeDataString(val);

            try
            {
                // 로그 파일에 기록
                string logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "log.txt");
                string logEntry = $"[{time}] {val}{Environment.NewLine}";
                File.AppendAllText(logPath, logEntry);

                // API 요청
                var request = WebRequest.Create(url);
                request.Method = "GET";
                using (var response = (HttpWebResponse)request.GetResponse())
                {
                    if (response.StatusCode == HttpStatusCode.OK)
                    {
                        // 정상 처리됨
                        File.AppendAllText(logPath, $"[{time}] API 응답 성공: {response.StatusCode}{Environment.NewLine}");
                    }
                    else
                    {
                        File.AppendAllText(logPath, $"[{time}] API 응답 오류: {response.StatusCode}{Environment.NewLine}");
                    }
                }
            }
            catch (Exception ex)
            {
                try
                {
                    string logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "log.txt");
                    File.AppendAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] API 요청 예외: {ex.Message}{Environment.NewLine}");
                }
                catch { }
            }
        }

    }
}