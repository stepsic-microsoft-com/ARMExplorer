﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Hyak.ApiModel;
using Hyak.ServiceModel;
using Newtonsoft.Json.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Web;
using System.Threading.Tasks;

namespace ARMExplorer.Controllers
{
    public static class HyakUtils
    {
        static object _lock = new object();
        static Dictionary<Type, JArray[]> _operations = new Dictionary<Type, JArray[]>();
        static JArray _speclessCsmApis = null;

        static HyakUtils()
        {
            ServiceType.ResetDefinedTypes();
        }

        public static string CSMUrl
        {
            get;
            set;
        }

        public static JArray GetOperationsAsync<T>(bool hidden)
        {
            lock (_lock)
            {
                JArray[] cache;
                if (_operations.TryGetValue(typeof(T), out cache))
                {
                    return cache[hidden ? 1 : 0];
                }
            }

            var service = ApiModeler.Instantiate(typeof(T));

            JArray array = new JArray();
            JArray skip = new JArray();
            foreach (var method in service.Methods.Values)
            {
                GenerateMethod(ShouldSkip(method) ? skip : array, method);
            }

            foreach (var operation in service.ServiceOperations)
            {
                foreach (var method in operation.Value.Methods.Values)
                {
                    GenerateMethod(ShouldSkip(method) ? skip : array, method);
                }
            }

            lock (_lock)
            {
                _operations[typeof(T)] = new[] { array, skip };
            }

            return array;
        }

        public static async Task<JArray> GetSpeclessCsmOperationsAsync()
        {
            if (_speclessCsmApis == null)
            {
                _speclessCsmApis = new JArray();
                using (var client = GetClient())
                {
                    var response = await client.GetAsync(HyakUtils.CSMUrl + "/subscriptions?api-version=2014-04-01");
                    if (!response.IsSuccessStatusCode) return _speclessCsmApis;

                    dynamic subscriptions = await response.Content.ReadAsAsync<JObject>();
                    if (subscriptions.value.Count == 0) return _speclessCsmApis;

                    var subId = subscriptions.value[0].subscriptionId;
                    response = await client.GetAsync(HyakUtils.CSMUrl + "/subscriptions/" + subId + "/providers?api-version=2014-04-01");
                    if (!response.IsSuccessStatusCode) return _speclessCsmApis;

                    var providersList = (JArray)(await response.Content.ReadAsAsync<JObject>())["value"];
                    var template = HyakUtils.CSMUrl + "/subscriptions/{subscriptionId}/resourceGroups/{resourceGroupName}/providers/";
                    var fakeRequestBody = new { properties = new { }, location = string.Empty };
                    providersList.Where(p => !new[] {
                        "Microsoft.Web",
                        "Microsoft.Compute",
                        "Microsoft.Storage",
                        "Microsoft.Network"
                    }.Any(str => p["namespace"].ToString().IndexOf(str, StringComparison.OrdinalIgnoreCase) >= 0))
                     .Select(provider =>
                    {
                        return provider["resourceTypes"].Select((resourceType) =>
                        {
                            return new[] { JObject.FromObject(new
                        {
                            MethodName = "GET",
                            HttpMethod = "GET",
                            Url = template + provider["namespace"] + "/" + ((string)resourceType["resourceType"]).Split('/').Aggregate((a, b) => a + "/{name}/" + b),
                            ApiVersion = resourceType["apiVersions"].FirstOrDefault()
                        }),
                        JObject.FromObject(new
                        {
                            MethodName = "GET",
                            HttpMethod = "GET",
                            Url = template + provider["namespace"] + "/" + ((string)resourceType["resourceType"]).Split('/').Aggregate((a, b) => a + "/{name}/" + b) + "/{name}",
                            ApiVersion = resourceType["apiVersions"].FirstOrDefault()
                        }),
                        JObject.FromObject(new
                        {
                            MethodName = "CreateOrUpdate",
                            HttpMethod = "PUT",
                            RequestBody = fakeRequestBody,
                            Url = template + provider["namespace"] + "/" + ((string)resourceType["resourceType"]).Split('/').Aggregate((a, b) => a + "/{name}/" + b) + "/{name}",
                            ApiVersion = resourceType["apiVersions"].FirstOrDefault()
                        }),
                        JObject.FromObject(new
                        {
                            MethodName = "Delete",
                            HttpMethod = "DELETE",
                            RequestBody = fakeRequestBody,
                            Url = template + provider["namespace"] + "/" + ((string)resourceType["resourceType"]).Split('/').Aggregate((a, b) => a + "/{name}/" + b) + "/{name}",
                            ApiVersion = resourceType["apiVersions"].FirstOrDefault()
                        })};
                        });
                    }).SelectMany(i => i).SelectMany(i => i).ToList().ForEach(_speclessCsmApis.Add);
                }
            }
            AddMissingApis(_speclessCsmApis);
            return _speclessCsmApis;
        }

        private static bool ShouldSkip(IMethod method)
        {
            return new [] {
                "backup",
                "restore",
                "discover",
                "slotConfigNames",
                "metrics",
                "repository",
                "usages",
                "Clone",
                "GetOperation",
                "register",
                "unregister",
                "GetSubscription",
                "PutSubscription",
                "ListSubscriptionStorageAccounts",
                "CheckDnsNameAvailability"
            }.Any(str => method.Name.IndexOf(str, StringComparison.OrdinalIgnoreCase) >=0 );
        }

        private static void GenerateMethod(JArray array, IMethod method)
        {
            var json = new JObject();
            json["MethodName"] = method.Name;
            if (method.Documentation != null) json["doc"] = JObject.FromObject(method.Documentation);
            json["HttpMethod"] = method.HttpMethod.ToString().ToUpper();
            json["ApiVersion"] = method.Service.ApiVersionExpression == "2013-03-01" ? "2014-12-01-preview" : method.Service.ApiVersionExpression;

            if (method.RequestBodies.Count == 1)
            {
                var request = (RequestBody)method.RequestBodies.First().Value;
                var schema = GetJsonSchehma(request.SerializationFormat);
                json["RequestBody"] = schema;
                json["RequestBodyDoc"] = GetJsonSchehma(request.SerializationFormat, getDocumentation: true);
            }

            if (method.ResponseBodies.Count == 1)
            {
                var response = (ResponseBody)method.ResponseBodies.First().Value.First().Value;
                var schema = GetJsonSchehma(response.SerializationFormat);
                json["ResponseBody"] = schema;
                json["ResponseBodyDoc"] = GetJsonSchehma(response.SerializationFormat, getDocumentation: true);
            }

            var url = EvaluateExpression(BindingExpression.Bind(method, method.UrlExpression)).ToString();
            url = url.Contains('?') ? url.Substring(0, url.IndexOf('?')) : url;

            var urls = new List<string>();
            if (url.Contains('['))
            {
                urls.Add(url.Replace("[", String.Empty).Replace("]", String.Empty));
                urls.Add(Regex.Replace(url, "[[][^]]*[]]+", String.Empty));
            }
            else
            {
                urls.Add(url);
            }

            foreach (var item in urls)
            {
                var clone = json.DeepClone();
                clone["Url"] = item;
                array.Add(clone);
            }
        }

        private static JToken GetJsonSchehma(ISerializationBase serialization, bool getDocumentation = false)
        {
            var xmlValue = serialization as Hyak.ServiceModel.XmlElement;
            if (xmlValue != null)
            {
                return "(xml)";
            }

            var jsonValue = serialization as Hyak.ServiceModel.JsonValue;
            if (jsonValue != null)
            {
                var knownObjectType = jsonValue.Type as Hyak.ServiceModel.KnownObjectType;
                if (knownObjectType != null)
                {
                    var schema = new JObject();
                    foreach (var member in jsonValue.Members)
                    {
                        if ((member is JsonValue && ((JsonValue)member).PassThrough) ||
                            (member is JsonArray && ((JsonArray)member).PassThrough) ||
                            (member is JsonDictionary && ((JsonDictionary)member).PassThrough))
                        {
                            return GetJsonSchehma(member, getDocumentation);
                        }

                        schema[member.Name] = GetJsonSchehma(member, getDocumentation);
                    }
                    return schema;
                }

                var knownType = jsonValue.Type as Hyak.ServiceModel.KnownType;
                if (knownType != null && !getDocumentation)
                {
                    return GetJsonSchehma(knownType);
                }
                else if (knownType != null && getDocumentation)
                {
                    return jsonValue.PropertyBinding.Documentation != null
                        ? jsonValue.PropertyBinding.Documentation.Text
                        : string.Empty;
                }

                throw new InvalidOperationException("Should not reach here. jsonValue.Type  = " + jsonValue.Type);
            }

            var jsonArray = serialization as Hyak.ServiceModel.JsonArray;
            if (jsonArray != null)
            {
                var schema = new JArray();
                if (jsonArray.ElementFormat != null)
                {
                    schema.Add(GetJsonSchehma(jsonArray.ElementFormat, getDocumentation));
                }
                else
                {
                    var knownType = jsonArray.Type.GenericParameters[0] as Hyak.ServiceModel.KnownType;
                    if (knownType != null && !getDocumentation)
                    {
                        schema.Add(GetJsonSchehma(knownType));
                    }
                    else if (knownType != null && getDocumentation)
                    {
                        schema.Add(jsonArray.PropertyBinding.Documentation != null
                        ? jsonArray.PropertyBinding.Documentation.Text
                        : string.Empty);
                    }
                    else
                    {
                        throw new InvalidOperationException("Should not reach here. array's elementType  = " + jsonArray.Type.GenericParameters[0]);
                    }
                }

                return schema;
            }

            var jsonDict = serialization as Hyak.ServiceModel.JsonDictionary;
            if (jsonDict != null)
            {
                return new JObject();
            }

            throw new InvalidOperationException("Should not reach here for " + serialization.GetType());
        }

        private static JToken GetJsonSchehma(Hyak.ServiceModel.KnownType knownType)
        {
            var enumType = knownType as Hyak.ServiceModel.EnumType;
            if (enumType != null)
            {
                return String.Format("({0})", String.Join("|", enumType.Values.Keys.ToArray()));
            }

            if (Nullable.GetUnderlyingType(knownType.UnderlyingType) != null)
            {
                return String.Format("({0})", knownType.UnderlyingType.GenericTypeArguments.First().Name.ToLowerInvariant());
            }

            return String.Format("({0})", knownType.UnderlyingType.Name.ToLowerInvariant());
        }

        private static object EvaluateExpression(BindingExpression expression)
        {
            if (expression.ToString().Equals("{BaseUri}", StringComparison.OrdinalIgnoreCase))
            {
                return CSMUrl;
            }

            var concat = expression as ConcatenatedBindingExpression;
            if (concat != null)
            {
                var strb = new StringBuilder();
                foreach (var item in concat.Expressions)
                {
                    strb.Append(EvaluateExpression(item));
                }
                return strb.ToString();
            }

            var conditional = expression as ConditionalBindingExpression;
            if (conditional != null)
            {
                var strb = new StringBuilder();
                strb.Append('[');
                strb.Append(EvaluateExpression(conditional.ConcatenatedExpression));
                strb.Append(']');
                return strb.ToString();
            }

            var literal = expression as LiteralBindingExpression;
            if (literal != null)
            {
                return literal.Text;
            }

            var instance = expression as InstanceBindingExpression;
            if (instance != null)
            {
                return instance.Context;
            }

            var property = expression as PropertyPathBindingExpression;
            if (property != null)
            {
                var obj = EvaluateExpression(property.Expression);
                if (obj == null)
                {
                    throw new InvalidOperationException(property.Expression + " expression should not be null");
                }

                var prop = obj.GetType().GetProperty(property.PropertyName);
                if (prop == null)
                {
                    return "{" + Char.ToLowerInvariant(property.PropertyName[0]) + property.PropertyName.Substring(1) + "}";
                }

                var value = prop.GetValue(obj);
                return value;
            }

            var parameter = expression as ParameterBindingExpression;
            if (parameter != null)
            {
                var name = parameter.ToString().Split(new[] { '.', '{', '}' }, StringSplitOptions.RemoveEmptyEntries).Last();
                return "{" + Char.ToLowerInvariant(name[0]) + name.Substring(1) + "}";
            }

            var formatting = expression as FormattingBindingExpression;
            if (formatting != null)
            {
                return "{" + formatting.FormatString + "}";
            }

            throw new InvalidOperationException("Should not reach here. " + expression.GetType() + ", " + expression);
        }

        private static void AddMissingApis(JArray array)
        {
            var fakeRequestBody = new { properties = new { }, location = string.Empty };
            array.AddFirst(JObject.FromObject(new
            {
                MethodName = "Delete",
                HttpMethod = "DELETE",
                Url = HyakUtils.CSMUrl + "/subscriptions/{subscriptionId}/resourceGroups/{resourceGroupName}",
                ApiVersion = Utils.CSMApiVersion
            }));
            array.AddFirst(JObject.FromObject(new
            {
                MethodName = "Get",
                HttpMethod = "GET",
                Url = HyakUtils.CSMUrl + "/subscriptions/{subscriptionId}/resourceGroups/{resourceGroupName}",
                ApiVersion = Utils.CSMApiVersion
            }));
            array.AddFirst(JObject.FromObject(new
            {
                MethodName = "CreateOrUpdate",
                HttpMethod = "PUT",
                RequestBody = fakeRequestBody,
                Url = HyakUtils.CSMUrl + "/subscriptions/{subscriptionId}/resourceGroups/{resourceGroupName}",
                ApiVersion = Utils.CSMApiVersion
            }));
            array.AddFirst(JObject.FromObject(new
            {
                MethodName = "Get",
                HttpMethod = "GET",
                Url = HyakUtils.CSMUrl + "/subscriptions/{subscriptionId}/resourceGroups",
                ApiVersion = Utils.CSMApiVersion
            }));
            array.AddFirst(JObject.FromObject(new
            {
                MethodName = "Get",
                HttpMethod = "GET",
                Url = HyakUtils.CSMUrl + "/subscriptions",
                ApiVersion = Utils.CSMApiVersion
            }));
            array.AddFirst(JObject.FromObject(new
            {
                MethodName = "Get",
                HttpMethod = "GET",
                Url = HyakUtils.CSMUrl + "/subscriptions/{subscriptionId}",
                ApiVersion = Utils.CSMApiVersion
            }));
        }

        private static HttpClient GetClient()
        {
            var client = new HttpClient();
            client.BaseAddress = new Uri(Utils.GetCSMUrl(string.Empty));
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", HttpContext.Current.Request.Headers[Utils.X_MS_OAUTH_TOKEN]);
            client.DefaultRequestHeaders.Add("User-Agent", HttpContext.Current.Request.Url.Host);
            return client;
        }

        private static IEnumerable<T> WithoutLast<T>(this IEnumerable<T> source)
        {
            using (var e = source.GetEnumerator())
            {
                if (e.MoveNext())
                {
                    for (var value = e.Current; e.MoveNext(); value = e.Current)
                    {
                        yield return value;
                    }
                }
            }
        }
    }
}