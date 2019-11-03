//------------------------------------------------------------------------------
// <auto-generated>
//    This code was generated from a template.
//
//    Manual changes to this file may cause unexpected behaviour in your application.
//    Manual changes to this file will be overwritten if the code is regenerated.
// </auto-generated>
//------------------------------------------------------------------------------

namespace Opc.Ua.Gds.Server
{
    using System;
    using System.Collections.Generic;
    
    public partial class CertificateRequest
    {
        public int ID { get; set; }
        public System.Guid RequestId { get; set; }
        public int ApplicationId { get; set; }
        public int State { get; set; }
        public string CertificateGroupId { get; set; }
        public string CertificateTypeId { get; set; }
        public byte[] CertificateSigningRequest { get; set; }
        public string SubjectName { get; set; }
        public string DomainNames { get; set; }
        public string PrivateKeyFormat { get; set; }
        public string PrivateKeyPassword { get; set; }
        public string AuthorityId { get; set; }
    
        public virtual Application Application { get; set; }
    }
}
