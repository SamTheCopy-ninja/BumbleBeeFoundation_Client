﻿using BumbleBeeFoundation_Client.Models;
using BumbleBeeFoundation_Client.Services;
using iText.Kernel.Colors;
using iText.Kernel.Pdf;
using iText.Layout.Borders;
using iText.Layout.Element;
using iText.Layout.Properties;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Web;

namespace BumbleBeeFoundation_Client.Controllers
{
    public class DonorController : Controller
    {
        private readonly ILogger<DonorController> _logger;
        private readonly IEmailService _emailService;
        private readonly PayFastSettings _payFastSettings;
        private readonly HttpClient _httpClient;

        public DonorController(
            IConfiguration configuration,
            ILogger<DonorController> logger,
            IEmailService emailService,
            IHttpClientFactory httpClientFactory)
        {
            _logger = logger;
            _emailService = emailService;
            _payFastSettings = configuration.GetSection("PayFast").Get<PayFastSettings>();
            _httpClient = httpClientFactory.CreateClient("ApiHttpClient");
        }

        // Get data for the donor dashboard
        public async Task<IActionResult> Index()
        {
            try
            {
                var response = await _httpClient.GetAsync("api/Donor/FundingRequests");
                if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsStringAsync();
                    var fundingRequests = JsonConvert.DeserializeObject<List<FundingRequestViewModel>>(content);
                    return View(fundingRequests);
                }

                _logger.LogError($"API call failed with status code: {response.StatusCode}");
                return View(new List<FundingRequestViewModel>());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching funding requests");
                return View(new List<FundingRequestViewModel>());
            }
        }

        // GET action to display the donation form
        [HttpGet]
        public IActionResult Donate()
        {
            var model = new DonationViewModel();
            return View(model);
        }

        // Process the donation made by the user
        
        [HttpPost]
        public async Task<IActionResult> Donate(DonationViewModel model, IFormFile? documentUpload)
        {
            try
            {
                if (!ModelState.IsValid || !IsValidIDNumber(model.DonorIDNumber))
                {
                    ModelState.AddModelError("DonorIDNumber", "Invalid ID number.");
                    return View(model);
                }

                // Encrypt sensitive data before sending to the API
                model.DonorIDNumber = EncryptionHelper.Encrypt(model.DonorIDNumber);
                model.DonorTaxNumber = EncryptionHelper.Encrypt(model.DonorTaxNumber);

                // Save donation to API
                var donationId = await SaveDonationToApi(model, documentUpload);

                if (donationId == 0)
                {
                    ModelState.AddModelError(string.Empty, "Failed to process the donation.");
                    return View(model);
                }

                // Set donation ID for email
                model.DonationId = donationId;

                // Send email notification asynchronously without waiting
                _ = _emailService.SendDonationNotificationAsync(model)
                    .ContinueWith(t =>
                    {
                        if (t.IsFaulted)
                            _logger.LogError(t.Exception, "Failed to send donation notification email.");
                    });

                // Pass the data to a view
                var payFastData = new PayFastViewModel
                {
                    MerchantId = _payFastSettings.MerchantId,
                    MerchantKey = _payFastSettings.MerchantKey,
                    NameFirst = model.DonorName?.Trim(),
                    EmailAddress = model.DonorEmail?.Trim(),
                    PaymentId = donationId.ToString(),
                    Amount = model.DonationAmount.ToString("F2", CultureInfo.InvariantCulture),
                    ItemName = $"Funding Donation - {model.DonationType}".Trim(),
                    PaymentMethod = model.DonationType == "Monthly" ? "eft" : "",
                    Signature = GeneratePayFastSignature(model, donationId),
                    PayFastUrl = _payFastSettings.UseSandbox
                        ? "https://sandbox.payfast.co.za/eng/process"
                        : "https://www.payfast.co.za/eng/process"
                };

                return View("ProcessPayment", payFastData);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing donation");
                ModelState.AddModelError(string.Empty, "An error occurred while processing your donation.");
                return View(model);
            }
        }

        private string GeneratePayFastSignature(DonationViewModel model, int donationId)
        {
            var payFastRequest = new PayFastRequest
            {
                merchant_id = _payFastSettings.MerchantId,
                merchant_key = _payFastSettings.MerchantKey,
                name_first = model.DonorName?.Trim(),
                email_address = model.DonorEmail?.Trim(),
                m_payment_id = donationId.ToString(),
                amount = model.DonationAmount.ToString("F2", CultureInfo.InvariantCulture),
                item_name = $"Funding Donation - {model.DonationType}".Trim(),
                payment_method = model.DonationType == "Monthly" ? "eft" : ""
            };

            return payFastRequest.GenerateSignature(_payFastSettings.PassPhrase);
        }

        // Save the donation information to the database
        private async Task<int> SaveDonationToApi(DonationViewModel model, IFormFile? documentUpload)
        {
            var content = new MultipartFormDataContent();

            // Add model properties
            content.Add(new StringContent(model.DonationType), "DonationType");
            content.Add(new StringContent(model.DonationAmount.ToString()), "DonationAmount");
            content.Add(new StringContent(model.DonorName), "DonorName");
            content.Add(new StringContent(model.DonorIDNumber), "DonorIDNumber");
            content.Add(new StringContent(model.DonorTaxNumber), "DonorTaxNumber");
            content.Add(new StringContent(model.DonorEmail), "DonorEmail");
            content.Add(new StringContent(model.DonorPhone), "DonorPhone");

            // Add document if present
            if (documentUpload != null && documentUpload.Length > 0)
            {
                var fileStream = documentUpload.OpenReadStream();
                var fileContent = new StreamContent(fileStream);
                fileContent.Headers.ContentType = new MediaTypeHeaderValue(documentUpload.ContentType);
                content.Add(fileContent, "documentUpload", documentUpload.FileName);
            }

            // Call API
            var response = await _httpClient.PostAsync("api/Donor/Donate", content);
            var responseContent = await response.Content.ReadAsStringAsync();
            var apiResponse = JsonConvert.DeserializeObject<ApiResponse<DonationResponse>>(responseContent);

            if (response.IsSuccessStatusCode && apiResponse?.Success == true && apiResponse.Data != null)
            {
                return apiResponse.Data.DonationId;
            }

            _logger.LogError($"API call failed: {apiResponse?.Message ?? "Unknown error"}");
            return 0;
        }

        // Check if the user ID number is the correct length
        private bool IsValidIDNumber(string idNumber)
        {
            return idNumber.Length == 13 && idNumber.All(char.IsDigit);
        }

        // Confirm the donation to the user
        public async Task<IActionResult> DonationConfirmation(int id)
        {
            var response = await _httpClient.GetAsync($"api/Donor/Donation/{id}");
            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync();
                var donation = JsonConvert.DeserializeObject<DonationViewModel>(content);
                return View(donation);
            }

            return NotFound();
        }

        // Allow a user to view their history of donations
        public async Task<IActionResult> DonationHistory()
        {
            string userEmail = HttpContext.Session.GetString("UserEmail");
            
            if (string.IsNullOrEmpty(userEmail))
            {
                return RedirectToAction("Login", "Account");
            }

            var response = await _httpClient.GetAsync($"api/Donor/Donations/User/{HttpUtility.UrlEncode(userEmail)}");
            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync();
                var donations = JsonConvert.DeserializeObject<List<DonationViewModel>>(content);
                return View(donations);
            }

            return View(new List<DonationViewModel>());
        }

        // Allow the user to download their history as a PDF
        public async Task<IActionResult> DownloadDonationHistory()
        {
            string userEmail = HttpContext.Session.GetString("UserEmail");
            string Firstname = HttpContext.Session.GetString("FirstName");
            string LastName = HttpContext.Session.GetString("LastName");

            if (string.IsNullOrEmpty(userEmail))
            {
                return RedirectToAction("Login", "Account");
            }

            var response = await _httpClient.GetAsync($"api/Donor/Donations/User/{HttpUtility.UrlEncode(userEmail)}");
            if (!response.IsSuccessStatusCode)
            {
                return RedirectToAction(nameof(DonationHistory));
            }

            var content = await response.Content.ReadAsStringAsync();
            var donations = JsonConvert.DeserializeObject<List<DonationViewModel>>(content);

            using (var memoryStream = new MemoryStream())
            {
                var writerProperties = new WriterProperties();
                using (var pdfWriter = new PdfWriter(memoryStream, writerProperties))
                {
                    using (var pdfDoc = new PdfDocument(pdfWriter))
                    {
                        using (var document = new iText.Layout.Document(pdfDoc))
                        {
                           
                            var headerColor = new DeviceRgb(30, 78, 120);  
                            var borderColor = new DeviceRgb(200, 200, 200);  
                            var outerBorderColor = new DeviceRgb(150, 150, 150); 

                            // Main container
                            var mainContainer = new Div()
                                .SetBorder(new SolidBorder(outerBorderColor, 2))
                                .SetPadding(20);

                            // Header Section
                            mainContainer.Add(new Paragraph("Bumble Bee Foundation")
                                .SetFontSize(18)
                                .SetBold()
                                .SetFontColor(headerColor)
                                .SetTextAlignment(TextAlignment.CENTER)
                                .SetMarginBottom(20));

                            // Report Title and Date
                            mainContainer.Add(new Paragraph($"Donation History Report for {Firstname} {LastName} - {userEmail}")
                                .SetFontSize(12)
                                .SetFontColor(ColorConstants.DARK_GRAY)
                                .SetTextAlignment(TextAlignment.CENTER)
                                .SetItalic()
                                .SetMarginBottom(10));

                            mainContainer.Add(new Paragraph($"Report Generated: {DateTime.Now:yyyy-MM-dd}")
                                .SetFontSize(10)
                                .SetTextAlignment(TextAlignment.CENTER)
                                .SetMarginBottom(20));

                            // Donation Table
                            Table table = new Table(new float[] { 2, 2, 2, 2, 3 }, true);
                            table.SetWidth(UnitValue.CreatePercentValue(100))
                                 .SetBorder(Border.NO_BORDER);

                            // Header Row
                            var headerBackgroundColor = new DeviceRgb(240, 240, 240);
                            string[] headers = { "Donation ID", "Date", "Type", "Amount", "Donor Name" };

                            foreach (var headerText in headers)
                            {
                                Cell headerCell = new Cell().Add(new Paragraph(headerText))
                                    .SetBackgroundColor(headerBackgroundColor)
                                    .SetTextAlignment(TextAlignment.CENTER)
                                    .SetBold()
                                    .SetFontColor(headerColor)
                                    .SetBorder(Border.NO_BORDER)
                                    .SetPadding(5);
                                table.AddHeaderCell(headerCell);
                            }

                            // Table Rows
                            bool alternate = false;
                            foreach (var donation in donations)
                            {
                                var rowBackgroundColor = alternate ? new DeviceRgb(248, 248, 248) : ColorConstants.WHITE;
                                alternate = !alternate;

                                table.AddCell(new Cell().Add(new Paragraph(donation.DonationId.ToString()))
                                    .SetBackgroundColor(rowBackgroundColor)
                                    .SetTextAlignment(TextAlignment.CENTER)
                                    .SetBorder(new SolidBorder(borderColor, 0.5f))
                                    .SetPadding(5));

                                table.AddCell(new Cell().Add(new Paragraph(donation.DonationDate.ToString("yyyy-MM-dd")))
                                    .SetBackgroundColor(rowBackgroundColor)
                                    .SetTextAlignment(TextAlignment.CENTER)
                                    .SetBorder(new SolidBorder(borderColor, 0.5f))
                                    .SetPadding(5));

                                table.AddCell(new Cell().Add(new Paragraph(donation.DonationType))
                                    .SetBackgroundColor(rowBackgroundColor)
                                    .SetTextAlignment(TextAlignment.CENTER)
                                    .SetBorder(new SolidBorder(borderColor, 0.5f))
                                    .SetPadding(5));

                                table.AddCell(new Cell().Add(new Paragraph(donation.DonationAmount.ToString("C", CultureInfo.GetCultureInfo("en-ZA"))))
                                    .SetBackgroundColor(rowBackgroundColor)
                                    .SetTextAlignment(TextAlignment.RIGHT)
                                    .SetBorder(new SolidBorder(borderColor, 0.5f))
                                    .SetPadding(5));

                                table.AddCell(new Cell().Add(new Paragraph(donation.DonorName))
                                    .SetBackgroundColor(rowBackgroundColor)
                                    .SetTextAlignment(TextAlignment.LEFT)
                                    .SetBorder(new SolidBorder(borderColor, 0.5f))
                                    .SetPadding(5));
                            }

                            mainContainer.Add(table);

                            // Footer Section
                            mainContainer.Add(new Paragraph("Thank you for your generous contributions to Bumble Bee Foundation. Your support helps us make a lasting impact.")
                                .SetTextAlignment(TextAlignment.CENTER)
                                .SetFontSize(12)
                                .SetItalic()
                                .SetFontColor(ColorConstants.DARK_GRAY)
                                .SetMarginTop(30));

                            // Add mainContainer to document
                            document.Add(mainContainer);
                        }
                    }
                }

                return File(memoryStream.ToArray(), "application/pdf", "DonationHistory.pdf");
            }
        }

        // Fetch a list of companies which have applied for funding
        public async Task<IActionResult> FundingRequests()
        {
            var response = await _httpClient.GetAsync("api/Donor/FundingRequests");
            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync();
                var fundingRequests = JsonConvert.DeserializeObject<List<FundingRequestViewModel>>(content);
                return View(fundingRequests);
            }

            return View(new List<FundingRequestViewModel>());
        }

        // Let the user search for companies
        [HttpGet]
        public async Task<IActionResult> SearchFundingRequests(string term)
        {
            var response = await _httpClient.GetAsync($"api/Donor/SearchFundingRequests?term={HttpUtility.UrlEncode(term)}");

            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync();
                var fundingRequests = JsonConvert.DeserializeObject<List<FundingRequestViewModel>>(content);

                // Serialize using Newtonsoft.Json and return as ContentResult
                var jsonResult = JsonConvert.SerializeObject(fundingRequests);
                return Content(jsonResult, "application/json", Encoding.UTF8);
            }

            // In case of an error, return an empty list serialized to JSON
            var emptyResult = JsonConvert.SerializeObject(new List<FundingRequestViewModel>());
            return Content(emptyResult, "application/json", Encoding.UTF8);
        }

    }


}
