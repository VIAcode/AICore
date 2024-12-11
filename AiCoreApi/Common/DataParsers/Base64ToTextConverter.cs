﻿using System.Text;
using AiCoreApi.Common.Extensions;

namespace AiCoreApi.Common.DataParsers
{ 
    public class Base64ToTextConverter
    {
        public static string ConvertToText(string base64File)
        {
            try
            {
                var byteArray = Convert.FromBase64String(base64File.StripBase64());
                return Encoding.UTF8.GetString(byteArray);
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }
    }
}