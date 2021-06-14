﻿using CollectSFData.Common;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace CollectSFDataGui.Server.Controllers
{
    [DisableRequestSizeLimit]
    public partial class UploadController : Controller
    {
        private readonly IWebHostEnvironment environment;

        public UploadController(IWebHostEnvironment environment)
        {
            this.environment = environment;
        }
                
        [HttpPost("upload/single")]
        public IActionResult Single(IFormFile file)
        {
            try
            {
                string jsonString = new StreamReader(file.OpenReadStream()).ReadToEnd();
                return Ok(jsonString);
                //return Ok(new { Completed = true, Json = jsonString });
            }
            catch (Exception ex)
            {
                return StatusCode(500, ex.Message);
            }
        }
    }
}