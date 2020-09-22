using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Tewr.Blazor.FileReader
{
    public partial class FileReaderJsInterop
    {
        private static readonly IReadOnlyDictionary<string, string> escapeScriptTextReplacements =
            new Dictionary<string, string> { { @"\", @"\\" }, { "\r", @"\r" }, { "\n", @"\n" }, { "'", @"\'" }, { "\"", @"\""" } };
        private readonly bool _needsInitialization = false;
        private readonly IFileReaderServiceOptions _options;
        private static long _readFileUnmarshalledCallIdSource;
        private static readonly Dictionary<long, TaskCompletionSource<int>> _readFileUnmarshalledCalls
            = new Dictionary<long, TaskCompletionSource<int>>();

        internal IJSRuntime CurrentJSRuntime { get; }

        public FileReaderJsInterop(IJSRuntime jsRuntime, IFileReaderServiceOptions options)
        {
            CurrentJSRuntime = jsRuntime;
            _options = options;
            _needsInitialization = options.InitializeOnFirstCall;
        }

        public async Task<bool> RegisterDropEvents(ElementReference elementReference, bool additive)
        {
            await EnsureInitializedAsync();
            return await CurrentJSRuntime.InvokeAsync<bool>($"FileReaderComponent.RegisterDropEvents", elementReference, additive);
        }

        public async Task<bool> UnregisterDropEvents(ElementReference elementReference)
        {
            await EnsureInitializedAsync();
            return await CurrentJSRuntime.InvokeAsync<bool>($"FileReaderComponent.UnregisterDropEvents", elementReference);
        }

        public async Task<AsyncDisposableStream> OpenFileStream(ElementReference elementReference, int index, IFileInfo fileInfo)
        {
            return new InteropFileStream(await OpenReadAsync(elementReference, index), fileInfo, this);
        }

        public async Task<IBase64Stream> OpenBase64Stream(ElementReference elementReference, int index, IFileInfo fileInfo)
        {
            return new Base64Stream(await OpenReadAsync(elementReference, index), fileInfo, this);
        }

        public async Task<int> GetFileCount(ElementReference elementReference)
        {
            await EnsureInitializedAsync();
            return (int)await CurrentJSRuntime.InvokeAsync<long>($"FileReaderComponent.GetFileCount", elementReference);
        }

        public async Task<int> ClearValue(ElementReference elementReference)
        {
            await EnsureInitializedAsync();
            return (int)await CurrentJSRuntime.InvokeAsync<long>($"FileReaderComponent.ClearValue", elementReference);
        }

        public async Task<IFileInfo> GetFileInfoFromElement(ElementReference elementReference, int index)
        {
            return await CurrentJSRuntime.InvokeAsync<FileInfo>($"FileReaderComponent.GetFileInfoFromElement", elementReference, index);
        }

        public async Task EnsureInitializedAsync(bool force = false)
        {
            if (!_needsInitialization && !force)
            {
                return;
            }

            await Initialize();
        }

        private async Task<int> OpenReadAsync(ElementReference elementReference, int fileIndex)
        {
            return (int)await CurrentJSRuntime.InvokeAsync<long>($"FileReaderComponent.OpenRead", elementReference, fileIndex);
        }

        private async Task<bool> DisposeStream(int fileRef)
        {
            return await CurrentJSRuntime.InvokeAsync<bool>($"FileReaderComponent.Dispose", fileRef);
        }

        private async Task<int> ReadFileAsync(int fileRef, byte[] buffer, long position, long bufferOffset, int count, CancellationToken cancellationToken)
        {
            if (this._options.UseWasmSharedBuffer)
            {
                return await ReadFileUnmarshalledAsync(fileRef, buffer, position, bufferOffset, count, cancellationToken);
            }

            return await ReadFileMarshalledAsync(fileRef, buffer, position, bufferOffset, count, cancellationToken);
        }

        private async Task<int> ReadFileMarshalledAsync(
            int fileRef, byte[] buffer, long position, long bufferOffset, int count,
            CancellationToken cancellationToken)
        {
            var result = await ReadFileMarshalledBase64Async(fileRef, position, count, cancellationToken);
            
            var bytesRead = 0;
            if (!string.IsNullOrEmpty(result))
            {
                var byteResult = Convert.FromBase64String(result);
                bytesRead = byteResult.Length;
                Array.Copy(byteResult, 0, buffer, bufferOffset, bytesRead);
            }

            return bytesRead;
        }

        private async Task<string> ReadFileMarshalledBase64Async(
            int fileRef, long position, int count,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var data = await CurrentJSRuntime.InvokeAsync<string>(
                $"FileReaderComponent.ReadFileMarshalledAsync", cancellationToken,
                new { position, count, fileRef });

            return data;

        }

        static bool isRetry = false;
        private async Task<int> ReadFileUnmarshalledAsync(
            int fileRef, byte[] buffer, long position, long bufferOffset, int count,
            CancellationToken cancellationToken)
        {
            var taskCompletionSource = new TaskCompletionSource<int>();
            var taskId = ++_readFileUnmarshalledCallIdSource;
            _readFileUnmarshalledCalls[taskId] = taskCompletionSource;
            cancellationToken.Register(() => taskCompletionSource.TrySetCanceled());

            // Temporary buffer as whatever will be shared with js
            // might be corrupted under gc pressure
            var pool = ArrayPool<byte>.Shared;
            var tempBuffer = pool.Rent(buffer.Length);
            for (int i = 0; i < buffer.Length; i++)
            {
                tempBuffer[i] = 0;
            }
            tempBuffer[0] = (byte)(taskId%255);
            ((IJSUnmarshalledRuntime) CurrentJSRuntime)
                .InvokeUnmarshalled<ReadFileParams, int>(
                $"FileReaderComponent.ReadFileUnmarshalledAsync",
                new ReadFileParams { 
                    Buffer = tempBuffer, 
                    BufferOffset = bufferOffset, 
                    Count = count, 
                    FileRef = fileRef,
                    Position = position,
                    TaskId = taskId
                });
            
            var bytesRead = await taskCompletionSource.Task;

            var tempBuffer2 = pool.Rent(buffer.Length);
            var testCall = await ReadFileMarshalledAsync(fileRef, tempBuffer2, position, bufferOffset, count, cancellationToken);
            var testEqual = ByteArrayCompare(tempBuffer, tempBuffer2);
            if (!testEqual)
            {
                Console.Error.WriteLine($"******** Broken! Params was " +
                    $"{nameof(bufferOffset)} {bufferOffset} {nameof(count)} {count} {nameof(fileRef)} {fileRef} " +
                    $"{nameof(position)} {position} {nameof(taskId)} {taskId}");

                Console.Error.WriteLine($"******** unmanaged {string.Join(" ", tempBuffer)}");
                Console.Error.WriteLine($"********   managed {string.Join(" ", tempBuffer2)}");

                // Can I try again and succeed??
                if (!isRetry == true)
                {
                    isRetry = true;
                    return await ReadFileUnmarshalledAsync(fileRef, buffer, position, bufferOffset, count, cancellationToken);
                }
                throw new InvalidOperationException("Call was broken");
            }


            Array.Copy(tempBuffer, bufferOffset, buffer, bufferOffset, bytesRead);
            pool.Return(tempBuffer);
            pool.Return(tempBuffer2);
            if (isRetry)
            {
                throw new InvalidOperationException("Call was broken, but was correct after retrying!!");
            }
            return bytesRead;
        }

        static bool ByteArrayCompare(byte[] a1, byte[] a2)
        {
            if (a1.Length != a2.Length)
                return false;

            for (int i = 0; i < a1.Length; i++)
                if (a1[i] != a2[i])
                    return false;

            return true;
        }

        [JSInvokable(nameof(EndReadFileUnmarshalledAsyncResult))]
        public static void EndReadFileUnmarshalledAsyncResult(long taskId, int bytesRead)
        {
            if (!_readFileUnmarshalledCalls.TryGetValue(taskId, out var taskCompletionSource)) {
                Console.Error.WriteLine($"{nameof(EndReadFileUnmarshalledAsyncResult)}: Unknown {nameof(taskId)} '{taskId}'");
                return;
            }

            _readFileUnmarshalledCalls.Remove(taskId);
            taskCompletionSource.SetResult(bytesRead);
        }


        [JSInvokable(nameof(EndReadFileUnmarshalledAsyncError))]
        public static void EndReadFileUnmarshalledAsyncError(long taskId, string error)
        {
            if (!_readFileUnmarshalledCalls.TryGetValue(taskId, out var taskCompletionSource))
            {
                Console.Error.WriteLine($"{nameof(EndReadFileUnmarshalledAsyncError)}: Unknown {nameof(taskId)} '{taskId}'");
                return;
            }

            _readFileUnmarshalledCalls.Remove(taskId);
            taskCompletionSource.SetException(new BrowserFileReaderException(error));
        }

        private async Task Initialize()
        {
            var isLoaded = await IsLoaded();
            if (isLoaded)
            {
                return;
            }

            string scriptContent;
            using (var stream = this.GetType().Assembly.GetManifestResourceStream("blazor:js:FileReaderComponent.js"))
            {
                using var streamReader = new StreamReader(stream);
                scriptContent = await streamReader.ReadToEndAsync();
            }

            // Load the script
            await ExecuteRawScriptAsync<object>(scriptContent);
            var loaderLoopBreaker = 0;
            while (!await IsLoaded())
            {
                loaderLoopBreaker++;
                await Task.Delay(100);

                // Fail after 3s not to block and hide any other possible error
                if (loaderLoopBreaker > 25)
                {
                    throw new InvalidOperationException("Unable to initialize FileReaderComponent script");
                }
            }
        }

        private async Task<bool> IsLoaded()
        {
            return await CurrentJSRuntime.InvokeAsync<bool>("window.hasOwnProperty", "FileReaderComponent");
        }

        private async Task<T> ExecuteRawScriptAsync<T>(string scriptContent)
        {
            scriptContent = escapeScriptTextReplacements.Aggregate(scriptContent, (r, pair) => r.Replace(pair.Key, pair.Value));
            var blob = $"URL.createObjectURL(new Blob([\"{scriptContent}\"],{{ \"type\": \"text/javascript\"}}))";
            var bootStrapScript = $"(function(){{var d = document; var s = d.createElement('script'); s.src={blob}; s.async=false; d.head.appendChild(s); d.head.removeChild(s);}})();";
            return await CurrentJSRuntime.InvokeAsync<T>("eval", bootStrapScript);
        }
    }
}
