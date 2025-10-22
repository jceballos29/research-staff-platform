using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace YourNamespace.Models
{
    // ========== ENUMS ==========
    
    public enum ProposalStatus
    {
        Pending,
        Approved,
        Rejected,
        Dismissal
    }

    public enum ContentType
    {
        Evidence,
        Training,
        Absence
    }

    public enum TrackingStatus
    {
        Draft,
        Pending,
        Approved,
        Rejected
    }

    // ========== ENTITIES ==========

    public class Customer
    {
        [Key]
        public string Id { get; set; }
        
        [Required]
        [MaxLength(200)]
        public string Name { get; set; }
        
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }

        // Navigation properties
        public virtual ICollection<Service> Services { get; set; } = new List<Service>();
    }

    public class Service
    {
        [Key]
        public string Id { get; set; }
        
        [Required]
        public string CustomerId { get; set; }
        
        [Required]
        [MaxLength(500)]
        public string Description { get; set; }
        
        [Required]
        public DateTime FiscalYearStart { get; set; }
        
        public bool HasTrimestralEvidences { get; set; }
        
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }

        // Navigation properties
        [ForeignKey(nameof(CustomerId))]
        public virtual Customer Customer { get; set; }
        
        public virtual ICollection<Resource> Resources { get; set; } = new List<Resource>();
    }

    public class Member
    {
        [Key]
        public string Id { get; set; }
        
        [Required]
        [MaxLength(200)]
        public string FullName { get; set; }
        
        [MaxLength(200)]
        public string Email { get; set; }
        
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }

        // Navigation properties
        public virtual ICollection<Resource> Resources { get; set; } = new List<Resource>();
    }

    public class Resource
    {
        [Key]
        public string Id { get; set; }
        
        [Required]
        public string ServiceId { get; set; }
        
        [Required]
        public string MemberId { get; set; }
        
        [Required]
        public ProposalStatus ProposalStatus { get; set; }
        
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }

        // Navigation properties
        [ForeignKey(nameof(ServiceId))]
        public virtual Service Service { get; set; }
        
        [ForeignKey(nameof(MemberId))]
        public virtual Member Member { get; set; }
        
        public virtual ICollection<ExclusivePeriod> Periods { get; set; } = new List<ExclusivePeriod>();
        public virtual ICollection<Tracking> Trackings { get; set; } = new List<Tracking>();
    }

    public class ExclusivePeriod
    {
        [Key]
        public string Id { get; set; }
        
        [Required]
        public string ResourceId { get; set; }
        
        [Required]
        public DateTime StartDate { get; set; }
        
        public DateTime? EndDate { get; set; }
        
        [MaxLength(500)]
        public string Description { get; set; }
        
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }

        // Navigation properties
        [ForeignKey(nameof(ResourceId))]
        public virtual Resource Resource { get; set; }
    }

    public class Tracking
    {
        [Key]
        public string Id { get; set; }
        
        [Required]
        public string ResourceId { get; set; }
        
        [Required]
        public int Month { get; set; } // Para trimestres: 1-4, Para meses: 1-12
        
        [Required]
        public int Year { get; set; }
        
        [Required]
        public ContentType ContentType { get; set; }
        
        [Required]
        public TrackingStatus TrackingApproveStatus { get; set; }
        
        [Required]
        [MaxLength(100)]
        public string Schedule { get; set; } // Ej: "4/2025 - 6/2025" o "4/2025"
        
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }

        // Navigation properties
        [ForeignKey(nameof(ResourceId))]
        public virtual Resource Resource { get; set; }
    }
}