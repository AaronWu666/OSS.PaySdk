﻿#region Copyright (C) 2017 Kevin (OSS开源作坊) 公众号：osscoder

/***************************************************************************
*　　	文件功能描述：微信支付模快 —— 请求基类
*
*　　	创建人： Kevin
*       创建人Email：1985088337@qq.com
*    	创建日期：2017-2-23
*       
*****************************************************************************/

#endregion

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;
using System.Xml;
using OSS.Common.ComModels;
using OSS.Common.ComModels.Enums;
using OSS.Common.Encrypt;
using OSS.Common.Plugs;
using OSS.Common.Plugs.LogPlug;
using OSS.Http.Extention;
using OSS.Http.Mos;
using OSS.PaySdk.Wx.SysTools;

namespace OSS.PaySdk.Wx
{
    /// <summary>
    ///  微信支付基类
    /// </summary>
    public abstract class WxPayBaseApi:BaseConfigProvider<WxPayCenterConfig,WxPayBaseApi>
    {
        /// <summary>
        /// 微信api接口地址
        /// </summary>
        protected const string m_ApiUrl = "https://api.mch.weixin.qq.com";
     
        #region  处理基本配置

        /// <summary>
        /// 构造函数
        /// </summary>
        /// <param name="config"></param>
        protected WxPayBaseApi(WxPayCenterConfig config) : base(config)
        {
            ModuleName = WxPayConfigProvider.ModuleName;
        }

        #endregion

        #region  调用基础请求方法

        /// <summary>
        /// 处理远程请求方法，并返回需要的实体
        /// </summary>
        /// <typeparam name="T">需要返回的实体类型</typeparam>
        /// <param name="request">远程请求组件的request基本信息</param>
        /// <param name="funcFormat">获取实体格式化方法</param>
        /// <param name="client">自定义请求客户端，当前主要是因为标准库没有提供证书设置选项，所以通过上层运行时传入设置委托，在使用证书的子类中构造客户端传入 </param>
        /// <returns>实体类型</returns>
        protected async Task<T> RestCommonAsync<T>(OsHttpRequest request,
            Func<HttpResponseMessage, Task<T>> funcFormat = null, HttpClient client = null)
            where T : WxPayBaseResp, new()
        {
            var t = default(T);
            try
            {
                var resp = await request.RestSend(client);
                if (resp.IsSuccessStatusCode)
                {
                    if (funcFormat != null)
                        t = await funcFormat(resp);
                    else
                    {
                        var contentStr = await resp.Content.ReadAsStringAsync();
                        t = GetRespResult<T>(contentStr);
                    }
                }
            }
            catch (Exception ex)
            {
                var errorKey = LogUtil.Error(string.Concat("基类请求出错，错误信息：", ex.Message), "RestCommon", ModuleNames.PayCenter);
                t = new T { ret = -1, msg = string.Concat("当前请求出错，错误码：", errorKey) };
            }

            if (t == null || !t.IsSuccess())
                return t ?? new T {ret = (int) ResultTypes.ObjectNull, msg = "未发现结果信息！"};
            
            if (t.return_code.ToUpper() == "SUCCESS" 
                && t.result_code.ToUpper() == "SUCCESS")
                return t;
  
            t.ret = -1;
            t.msg = string.Concat(t.return_msg, t.err_code_des);
            return t;
        }

        /// <summary>
        /// 获取响应结果
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="contentStr"></param>
        /// <returns></returns>
        protected T GetRespResult<T>(string contentStr) where T : WxPayBaseResp, new()
        {
            XmlDocument resultXml = null;
            var dics = SysUtil.ChangXmlToDir(contentStr, ref resultXml);

            if (!dics.ContainsKey("sign"))
                return new T{ret = (int)ResultTypes.ParaError,msg = "当前结果签名信息不存在！"};

            var t = new T {RespXml = resultXml};
            t.FromResContent(dics);
            
            var signStr = GetSign(GetSignContent(dics));
            if (signStr == t.sign)
                return t;

            t.ret = (int)ResultTypes.ParaError;
            t.msg = "返回的结果签名（sign）不匹配";

            return t;
        }

        /// <summary>
        ///   post 支付接口相关请求
        /// </summary>
        /// <typeparam name="T">返回参数类型</typeparam>
        /// <param name="addressUrl">接口地址</param>
        /// <param name="xmlDirs">请求参数的排序字典（不包括：appid,mch_id,sign。 会自动补充）</param>
        /// <param name="funcFormat"></param>
        /// <param name="client">自定义请求客户端，当前主要是因为标准库没有提供证书设置选项，所以通过上层运行时传入设置委托，在使用证书的子类中构造客户端传入</param>
        /// <param name="dirformat">生成签名后对字典发送前的操作，例如urlencode操作</param>
        /// <returns></returns>
        protected async Task<T> PostApiAsync<T>(string addressUrl, SortedDictionary<string, object> xmlDirs,
            Func<HttpResponseMessage, Task<T>> funcFormat = null,HttpClient client=null,Action<SortedDictionary<string, object>> dirformat=null) where T : WxPayBaseResp, new()
        {
            xmlDirs.Add("appid", ApiConfig.AppId);
            xmlDirs.Add("mch_id", ApiConfig.MchId);

            CompleteDicSign(xmlDirs);
            dirformat?.Invoke(xmlDirs);

            var req = new OsHttpRequest
            {
                HttpMothed = HttpMothed.POST,
                AddressUrl = addressUrl,
                CustomBody = xmlDirs.ProduceXml()
            };

            return await RestCommonAsync<T>(req, funcFormat,client);
        }

        /// <summary>
        ///  补充完善 字典sign签名
        ///     因为AppId的参数名称在不同接口中不同，所以不放在这里补充
        /// </summary>
        protected internal void CompleteDicSign(SortedDictionary<string, object> xmlDirs)
        {
            // 设置服务商子商户号信息 
            if (!string.IsNullOrEmpty(ApiConfig.sub_mch_id))
            {
                if (!string.IsNullOrEmpty(ApiConfig.sub_appid))
                    xmlDirs.Add("sub_appid", ApiConfig.sub_appid);

                xmlDirs.Add("sub_mch_id", ApiConfig.sub_mch_id);
            }

            var encStr = GetSignContent(xmlDirs);
            var sign = GetSign(encStr);
            xmlDirs.Add("sign", sign);
        }

        /// <summary> 生成签名,统一方法 </summary>
        /// <param name="encStr">不含key的参与签名串</param>
        /// <returns></returns>
        protected string GetSign(string encStr)
        {
            return Md5.EncryptHexString(string.Concat(encStr, "&key=", ApiConfig.Key)).ToUpper();
        }
        
        /// <summary> 获取签名内容字符串</summary>
        protected static string GetSignContent(SortedDictionary<string, object> xmlDirs)
        {
            var sb = new StringBuilder();

            foreach (var item in xmlDirs)
            {
                var value = item.Value?.ToString();
                if (item.Key == "sign" || string.IsNullOrEmpty(value)) continue;

                sb.Append(item.Key).Append("=").Append(value).Append("&");
            }

            var encStr = sb.ToString().TrimEnd('&');
            return encStr;
        }

        /// <summary>   接受微信支付通知后需要返回的信息 </summary>
        public string GetCallBackReturnXml(ResultMo res)
        {
            return string.Format($"<xml><return_code><![CDATA[{ (res.IsSuccess() ? "SUCCESS" : "FAIL")}]]></return_code><return_msg><![CDATA[{ res.msg}]]></return_msg></xml>");
        }
        #endregion
        
        private HttpClient _client;
        /// <summary>
        ///   获取设置了证书的HttpClient
        ///     如果是上下文配置模式，则每次都返回新值
        /// </summary>
        /// <returns></returns>
        protected internal HttpClient GetCertHttpClient()
        {
            if(ConfigMode!=ConfigProviderMode.Context && _client != null)
                return _client;

   
            var reqHandler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (msg, c, chain, sslErrors) => sslErrors == SslPolicyErrors.None
            };
            var cert = new X509Certificate2(ApiConfig.CertPath, ApiConfig.CertPassword);
            reqHandler.ClientCertificates.Add(cert);
            
            if (ConfigMode==ConfigProviderMode.Context)
                return new HttpClient(reqHandler);

            return _client = new HttpClient(reqHandler);
        }
        
        /// <inheritdoc />
        protected override WxPayCenterConfig GetDefaultConfig()
        {
            return WxPayConfigProvider.DefaultConfig;
        }
    }
}
