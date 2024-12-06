﻿using System.ComponentModel.DataAnnotations;

namespace BumbleBeeFoundation_Client.Models
{
    public class DonationViewModel
    {
        public int DonationId { get; set; }

        [Required]
        [Display(Name = "Donation Type")]
        public string DonationType { get; set; }

        [Required]
        [Display(Name = "Donation Amount")]
        [DataType(DataType.Currency)]
        public decimal DonationAmount { get; set; }

        [Required]
        [Display(Name = "Donor Name OR Company Name")]
        public string DonorName { get; set; }

        [Required]
        [Display(Name = "ID Number")]
        public string DonorIDNumber { get; set; }

        [Required]
        [Display(Name = "Tax Number")]
        public string DonorTaxNumber { get; set; }

        [Required]
        [EmailAddress]
        [Display(Name = "Email")]
        public string DonorEmail { get; set; }

        [Required]
        [Phone]
        [Display(Name = "Phone")]
        public string DonorPhone { get; set; }

        public DateTime DonationDate { get; set; }

        [Display(Name = "Document")]
        public IFormFile? DocumentUpload { get; set; }

        public byte[]? DocumentPath { get; set; }

    }

    public class DonationResponse
    {
        public int DonationId { get; set; }
        public bool Success { get; set; }
        public string Message { get; set; }
    }

    public class ApiResponse<T>
    {
        public bool Success { get; set; }
        public string Message { get; set; }
        public T Data { get; set; }
    }

}
