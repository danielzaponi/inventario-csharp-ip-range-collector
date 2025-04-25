using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Net;

class Program
{
    static async Task Main(string[] args)
    {
        Console.Write("Informe o range de IPs (ex: 192.168.1.1-192.168.1.100): ");
        string ipRange = Console.ReadLine()?.Trim();

        string baseUrl = "http://localhost:5062/api/Wmic/info/{IP}";
        string data = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string outputCsvPath = $"inventario_maquinas_{data}.csv";
        string errorLogPath = $"erros_{data}.txt";

        var ips = ParseIpRange(ipRange);
        if (ips.Count == 0)
        {
            Console.WriteLine("⚠️ Nenhum IP válido foi gerado. Verifique o formato do range.");
            return;
        }

        var camposPadrao = new List<string> {
            "ip", "manufacturer", "model", "serial_number", "processor", "operating_system",
            "total_memory", "free_memory", "mac_address", "hostname", "current_user", "location",
            "used_memory", "total_storage", "free_storage", "used_storage", "chassis_type","drives","execution_time", "status"
        };

        using var httpClient = new HttpClient();
        var erros = new List<string>();

        int maxConcurrency = 10;
        var throttler = new SemaphoreSlim(maxConcurrency);
        int total = ips.Count;
        int concluido = 0;
        object lockConsole = new();

        var tasks = ips.Select(async ip =>
        {
            await throttler.WaitAsync();
            try
            {
                Dictionary<string, string> registro = null;
                int tentativas = 0;

                while (tentativas < 3)
                {
                    tentativas++;
                    try
                    {
                        string url = baseUrl.Replace("{IP}", ip);
                        var response = await httpClient.GetAsync(url);

                        if (response.IsSuccessStatusCode)
                        {
                            string json = await response.Content.ReadAsStringAsync();
                            var data = JsonSerializer.Deserialize<Dictionary<string, object>>(json);

                            registro = new Dictionary<string, string> { ["ip"] = ip };

                            foreach (var campo in camposPadrao.Skip(1))
                            {
                                registro[campo] = data?.ContainsKey(campo) == true
                                    ? data[campo]?.ToString() ?? ""
                                    : "";
                            }
                            break;
                        }
                        else if (tentativas == 3)
                        {
                            erros.Add($"{ip}: StatusCode {response.StatusCode}");
                        }
                    }
                    catch (Exception ex)
                    {
                        if (tentativas == 3)
                            erros.Add($"{ip}: {ex.Message}");
                    }

                    await Task.Delay(500);
                }

                return registro;
            }
            finally
            {
                throttler.Release();
                lock (lockConsole)
                {
                    concluido++;
                    Console.Write($"\rConsultados: {concluido}/{total} - IP atual: {ip}");
                }
            }
        });

        var resultados = await Task.WhenAll(tasks);
        var registros = resultados.Where(r => r != null).ToList();

        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", camposPadrao));

        foreach (var registro in registros)
        {
            var linha = camposPadrao.Select(campo => $"\"{registro.GetValueOrDefault(campo, "").Replace("\"", "\"\"")}\"");
            sb.AppendLine(string.Join(",", linha));
        }

        await File.WriteAllTextAsync(outputCsvPath, sb.ToString(), Encoding.UTF8);
        Console.WriteLine($"\n✅ Inventário salvo em: {outputCsvPath}");

        if (erros.Any())
        {
            await File.WriteAllLinesAsync(errorLogPath, erros);
            Console.WriteLine($"⚠️  Erros salvos em: {errorLogPath}");
        }
    }

    static List<string> ParseIpRange(string range)
    {
        var partes = range.Split('-');
        if (partes.Length != 2)
            return new List<string>();

        if (!IPAddress.TryParse(partes[0], out var ipInicial) || !IPAddress.TryParse(partes[1], out var ipFinal))
            return new List<string>();

        var inicioBytes = ipInicial.GetAddressBytes();
        var fimBytes = ipFinal.GetAddressBytes();

        if (inicioBytes.Length != 4 || fimBytes.Length != 4)
            return new List<string>();

        var listaIps = new List<string>();
        uint start = BitConverter.ToUInt32(inicioBytes.Reverse().ToArray(), 0);
        uint end = BitConverter.ToUInt32(fimBytes.Reverse().ToArray(), 0);

        if (start > end)
            return new List<string>();

        for (uint ip = start; ip <= end; ip++)
        {
            var bytes = BitConverter.GetBytes(ip).Reverse().ToArray();
            listaIps.Add(new IPAddress(bytes).ToString());
        }

        return listaIps;
    }
}