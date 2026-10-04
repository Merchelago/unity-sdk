using System;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

namespace VhrGames.Sdk.Editor
{
    /// <summary>
    /// HTTP для окон Editor (работает и вне Play Mode): <see cref="UnityWebRequest"/>
    /// + опрос завершения через <see cref="EditorApplication.update"/>. Шлёт те же
    /// заголовки, что и SDK в игре: <c>Authorization: Bearer</c>, <c>X-Vhr-Game-Id</c>,
    /// <c>X-Vhr-Sdk-Version</c>.
    /// </summary>
    internal static class VhrEditorHttp
    {
        /// <summary>Ответ сервера. <see cref="Status"/> = 0 — запрос до сервера не дошёл.</summary>
        internal readonly struct Response
        {
            public readonly long Status;
            public readonly string Body;
            public readonly string Error;

            public Response(long status, string body, string error)
            {
                Status = status;
                Body = body;
                Error = error;
            }

            public bool Ok => Status >= 200 && Status < 300 && string.IsNullOrEmpty(Error);

            /// <summary>Краткое описание для UI: «200 OK» / «403 game_mismatch» / «нет связи: …».</summary>
            public string Short()
            {
                if (Status == 0) return "нет связи: " + (Error ?? "неизвестная ошибка");
                if (Ok) return Status + " OK";
                var code = VhrApiClient.TryReadServerCode(Body);
                return Status + (string.IsNullOrEmpty(code) ? string.Empty : " " + code);
            }
        }

        /// <summary>Отправляет запрос; <paramref name="done"/> вызывается на главном потоке ровно один раз.</summary>
        public static void Send(string method, string url, string bearer, string gameId, string jsonBody,
            Action<Response> done, int timeoutSeconds = 20)
        {
            UnityWebRequest req;
            try
            {
                req = new UnityWebRequest(url, method)
                {
                    downloadHandler = new DownloadHandlerBuffer(),
                    timeout = Mathf.Max(1, timeoutSeconds)
                };
                if (jsonBody != null)
                {
                    req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(jsonBody)) { contentType = "application/json" };
                    req.SetRequestHeader("Content-Type", "application/json");
                }
                req.SetRequestHeader("Accept", "application/json");
                req.SetRequestHeader("X-Vhr-Sdk-Version", VhrSdk.SdkVersion);
                if (!string.IsNullOrEmpty(gameId)) req.SetRequestHeader("X-Vhr-Game-Id", gameId);
                if (!string.IsNullOrEmpty(bearer)) req.SetRequestHeader("Authorization", "Bearer " + bearer);
                req.SendWebRequest();
            }
            catch (Exception e)
            {
                done?.Invoke(new Response(0, null, e.Message));
                return;
            }

            EditorApplication.CallbackFunction tick = null;
            tick = () =>
            {
                if (!req.isDone) return;
                EditorApplication.update -= tick;

                Response r;
                try
                {
                    var body = req.downloadHandler != null ? req.downloadHandler.text : null;
                    var transportFailed = req.result == UnityWebRequest.Result.ConnectionError ||
                                          req.result == UnityWebRequest.Result.DataProcessingError;
                    r = transportFailed
                        ? new Response(0, body, req.error ?? "connection_error")
                        : new Response(req.responseCode, body,
                            req.result == UnityWebRequest.Result.Success ? null : "HTTP " + req.responseCode);
                }
                catch (Exception e)
                {
                    r = new Response(0, null, e.Message);
                }
                finally
                {
                    req.Dispose();
                }

                try { done?.Invoke(r); }
                catch (Exception e) { Debug.LogException(e); }
            };
            EditorApplication.update += tick;
        }

        /// <summary>
        /// Загружает картинку (превью иконки) PNG/JPG. <c>null</c> при ошибке. Без
        /// модуля UnityWebRequestTexture — байты + <see cref="ImageConversion.LoadImage(Texture2D, byte[])"/>.
        /// </summary>
        public static void GetTexture(string url, Action<Texture2D> done)
        {
            UnityWebRequest req;
            try
            {
                req = new UnityWebRequest(url, "GET") { downloadHandler = new DownloadHandlerBuffer(), timeout = 20 };
                req.SendWebRequest();
            }
            catch
            {
                done?.Invoke(null);
                return;
            }

            EditorApplication.CallbackFunction tick = null;
            tick = () =>
            {
                if (!req.isDone) return;
                EditorApplication.update -= tick;
                Texture2D tex = null;
                try
                {
                    if (req.result == UnityWebRequest.Result.Success && req.downloadHandler?.data is { Length: > 0 } bytes)
                    {
                        tex = new Texture2D(2, 2);
                        if (!ImageConversion.LoadImage(tex, bytes))
                        {
                            UnityEngine.Object.DestroyImmediate(tex);
                            tex = null;
                        }
                    }
                }
                catch
                {
                    tex = null;
                }
                finally
                {
                    req.Dispose();
                }

                try { done?.Invoke(tex); }
                catch (Exception e) { Debug.LogException(e); }
            };
            EditorApplication.update += tick;
        }
    }
}
