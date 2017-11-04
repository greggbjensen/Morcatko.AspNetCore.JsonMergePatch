﻿using Microsoft.AspNetCore.JsonPatch.Operations;
using Microsoft.AspNetCore.Mvc.Formatters;
using Microsoft.AspNetCore.Mvc.Formatters.Json.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.ObjectPool;
using Microsoft.Net.Http.Headers;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Buffers;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Morcatko.AspNetCore.JsonMergePatch.Formatters
{
    internal class JsonMergePatchInputFormatter : JsonInputFormatter
    {
        private static readonly MediaTypeHeaderValue JsonMergePatchMediaType = MediaTypeHeaderValue.Parse(JsonMergePatchDocument.ContentType).CopyAsReadOnly();

        private readonly IArrayPool<char> _charPool;

        public JsonMergePatchInputFormatter(
            ILogger logger,
            JsonSerializerSettings serializerSettings,
            ArrayPool<char> charPool,
            ObjectPoolProvider objectPoolProvider)
            : base(logger, serializerSettings, charPool, objectPoolProvider)
        {
            this._charPool = new JsonArrayPool<char>(charPool);

            SupportedMediaTypes.Clear();
            SupportedMediaTypes.Add(JsonMergePatchMediaType);
        }

        private static bool ContainerIsIEnumerable(InputFormatterContext context) => context.ModelType.IsGenericType && (context.ModelType.GetGenericTypeDefinition() == typeof(IEnumerable<>));
        private static void AddOperation(JsonMergePatchDocument jsonMergePatchDocument, string pathPrefix, JObject jObject)
        {
            foreach (var jProperty in jObject)
            {
                if (jProperty.Value is JValue)
                    jsonMergePatchDocument.AddOperation(OperationType.Replace, pathPrefix + jProperty.Key, ((JValue)jProperty.Value).Value);
                else if (jProperty.Value is JArray)
                    jsonMergePatchDocument.AddOperation(OperationType.Replace, pathPrefix + jProperty.Key, ((JArray)jProperty.Value));
                else if (jProperty.Value is JObject)
                    AddOperation(jsonMergePatchDocument, pathPrefix + jProperty.Key + "/", (jProperty.Value as JObject));
            }
        }

        private JsonMergePatchDocument CreatePatchDocument(JsonSerializer jsonSerializer, Type jsonMergePatchType, Type modelType, JObject jObject)
        {
            var model = jObject.ToObject(modelType, jsonSerializer);
            var jsonMergePatchDocument = (JsonMergePatchDocument)Activator.CreateInstance(jsonMergePatchType, model);
            AddOperation(jsonMergePatchDocument, "/", jObject);
            jsonMergePatchDocument.ContractResolver = SerializerSettings.ContractResolver;
            return jsonMergePatchDocument;
        }

        public async override Task<InputFormatterResult> ReadRequestBodyAsync(InputFormatterContext context, Encoding encoding)
        {
            var request = context.HttpContext.Request;
            using (var streamReader = context.ReaderFactory(request.Body, encoding))
            {
                using (var jsonReader = new JsonTextReader(streamReader))
                {
                    jsonReader.ArrayPool = _charPool;
                    jsonReader.CloseInput = false;

                    var jsonMergePatchType = context.ModelType;
                    var container = (IList)null;

                    if (ContainerIsIEnumerable(context))
                    {
                        jsonMergePatchType = context.ModelType.GenericTypeArguments[0];
                        var listType = typeof(List<>);
                        var constructedListType = listType.MakeGenericType(jsonMergePatchType);
                        container = (IList)Activator.CreateInstance(constructedListType);
                    }
                    var modelType = jsonMergePatchType.GenericTypeArguments[0];


                    var jsonSerializer = CreateJsonSerializer();
                    try
                    {
                        var jToken = await JToken.LoadAsync(jsonReader);

                        switch (jToken)
                        {
                            case JObject jObject:
                                if (container != null)
                                    throw new ArgumentException("Received object when array was expected"); //This could be handled by returnin list with single item

                                var jsonMergePatchDocument = CreatePatchDocument(jsonSerializer, jsonMergePatchType, modelType, jObject);
                                return await InputFormatterResult.SuccessAsync(jsonMergePatchDocument);
                            case JArray jArray:
                                if (container == null)
                                    throw new ArgumentException("Received array when object was expected");
                                
                                foreach (var jObject in jArray.OfType<JObject>())
                                {
                                    container.Add(CreatePatchDocument(jsonSerializer, jsonMergePatchType, modelType, jObject));
                                }
                                return await InputFormatterResult.SuccessAsync(container);
                        }

                        return await InputFormatterResult.FailureAsync();

                    }
                    catch (Exception ex)
                    {
                        context.ModelState.TryAddModelError(context.ModelName, ex.Message);
                        return await InputFormatterResult.FailureAsync();
                    }
                    finally
                    {
                        ReleaseJsonSerializer(jsonSerializer);
                    }
                }
            }
        }


        public override bool CanRead(InputFormatterContext context)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            var jsonMergePatchType = context.ModelType;

            if (ContainerIsIEnumerable(context))
                jsonMergePatchType = context.ModelType.GenericTypeArguments[0];

            return (jsonMergePatchType.IsGenericType && (jsonMergePatchType.GetGenericTypeDefinition() == typeof(JsonMergePatchDocument<>)));
        }
    }
}
