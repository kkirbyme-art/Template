namespace DigitalSignature.Models;

public class AlternateSignatoryDto
{
    public int Id { get; set; }
    public int AlternateEid { get; set; }
    public int AlternateUserType { get; set; }
    public string? AlternateName { get; set; }
    public string? AlternateOffice { get; set; }
    public bool IsActive { get; set; }
    public bool IsPermanent { get; set; }
    public DateTime? DateFrom { get; set; }
    public DateTime? DateTo { get; set; }
}

public class AddAlternateRequest
{
    public int AlternateEid { get; set; }
    public int AlternateUserType { get; set; }
    public bool IsPermanent { get; set; }
    public DateTime? DateFrom { get; set; }
    public DateTime? DateTo { get; set; }
}

public class UpdateAlternateRequest
{
    public bool IsPermanent { get; set; }
    public DateTime? DateFrom { get; set; }
    public DateTime? DateTo { get; set; }
}

public class AlternateDocumentTypeDto
{
    public int Id { get; set; }
    public int DocTypeId { get; set; }
    public string? DocumentDescription { get; set; }
    public string? DocumentAbbr { get; set; }
}

public class AddAlternateDocumentTypeRequest
{
    public int AlterId { get; set; }
    public int DocTypeId { get; set; }
}

public class AlternatePrincipalDto
{
    public int PrincipalEid { get; set; }
    public int PrincipalUserType { get; set; }
    public string? PrincipalName { get; set; }
    public string? PrincipalOffice { get; set; }
}
