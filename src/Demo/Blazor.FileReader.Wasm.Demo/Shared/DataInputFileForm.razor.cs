using Microsoft.AspNetCore.Components;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Text.Json;

namespace Blazor.FileReader.Wasm.Demo.Shared
{
    public partial class DataInputFileForm
    {

        [Parameter] public string Label { get; set; } = "Select file (-s)";
        [Parameter] public string FileContent { get; set; } = "";
        [Parameter] public EventCallback OnUseLogs { get; set; }
        [Parameter] public List<SiteLog> LoadedData { get; set; } = new List<SiteLog>();
        [Parameter] public int SiteID { get; set; }
        ElementReference InputElement;
        private string _status;

        async Task FilesSelected()
        {
            LoadedData = new List<SiteLog>();
            _status = "Loading text from file (-s).. " + Environment.NewLine;
            foreach (var file in await fileReaderService.CreateReference(InputElement).EnumerateFilesAsync())
            {
                var fileInfo = await file.ReadFileInfoAsync();

                var filename = fileInfo.Name;
                _status += $"Loading {filename}, ";
                this.InvokeAsync(this.StateHasChanged);
                using (Stream stream = await file.OpenReadAsync())
                {                    
                    this.InvokeAsync(this.StateHasChanged);
                    FileContent = await ReadAllText(stream, Encoding.UTF8);
                }
                _status += $"loaded text from file with length {FileContent.Length}, ";
                this.InvokeAsync(this.StateHasChanged);
            }
        }

        public async Task<string> ReadAllText(Stream stream, Encoding encoding)
        {
            
            using (var reader = new StreamReader(stream, encoding))
            {
                var result = await reader.ReadToEndAsync();

                return result;
            }
        }

        private async Task OnConvertData()
        {
            if (!string.IsNullOrEmpty(FileContent))
            {
                _status += $"{Environment.NewLine}Parsing text to logs...";
                try
                {
                    LoadedData.AddRange(await ConvertJsonToSiteLogs(FileContent));
                    _status += $"{Environment.NewLine}Parsed data into  {LoadedData.Count} site logs. ";
                }
                catch (Exception ex)
                {
                    _status += $"{Environment.NewLine}Unable to convert file content:";
                    _status += ex.Message;
                }
            }
            else
            {
                _status += "There is no text loaded.";
            }
            this.InvokeAsync(this.StateHasChanged);
        }


        public async Task<List<SiteLog>> ConvertJsonToSiteLogs(string json)
        {
            return JsonSerializer.Deserialize<SiteLog[]>(json).ToList();
        }
    }
}
