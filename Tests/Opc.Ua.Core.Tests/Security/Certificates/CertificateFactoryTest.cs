/* ========================================================================
 * Copyright (c) 2005-2018 The OPC Foundation, Inc. All rights reserved.
 *
 * OPC Foundation MIT License 1.00
 * 
 * Permission is hereby granted, free of charge, to any person
 * obtaining a copy of this software and associated documentation
 * files (the "Software"), to deal in the Software without
 * restriction, including without limitation the rights to use,
 * copy, modify, merge, publish, distribute, sublicense, and/or sell
 * copies of the Software, and to permit persons to whom the
 * Software is furnished to do so, subject to the following
 * conditions:
 * 
 * The above copyright notice and this permission notice shall be
 * included in all copies or substantial portions of the Software.
 * THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
 * EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES
 * OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
 * NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT
 * HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY,
 * WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
 * FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR
 * OTHER DEALINGS IN THE SOFTWARE.
 *
 * The complete license agreement can be found here:
 * http://opcfoundation.org/License/MIT/1.00/
 * ======================================================================*/

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using NUnit.Framework;
using Opc.Ua.Security.Certificates;

namespace Opc.Ua.Core.Tests.Security.Certificates
{
    /// <summary>
    /// Tests for the CertificateFactory class.
    /// </summary>
    [TestFixture, Category("CertificateFactory")]
    [Parallelizable]
    [SetCulture("en-us")]
    public class CertificateFactoryTest
    {
        #region DataPointSources
        public class KeyHashPair : IFormattable
        {
            public KeyHashPair(ushort keySize, ushort hashSize)
            {
                KeySize = keySize;
                HashSize = hashSize;
            }

            public ushort KeySize;
            public ushort HashSize;

            public string ToString(string format, IFormatProvider formatProvider)
            {
                return $"{KeySize}-{HashSize}";
            }
        }

        public class KeyHashPairCollection : List<KeyHashPair>
        {
            public KeyHashPairCollection() { }
            public KeyHashPairCollection(IEnumerable<KeyHashPair> collection) : base(collection) { }
            public KeyHashPairCollection(int capacity) : base(capacity) { }
            public static KeyHashPairCollection ToJsonValidationDataCollection(KeyHashPair[] values)
            {
                return values != null ? new KeyHashPairCollection(values) : new KeyHashPairCollection();
            }

            public void Add(ushort keySize, ushort hashSize)
            {
                Add(new KeyHashPair(keySize, hashSize));
            }
        }

        [DatapointSource]
        public KeyHashPair[] KeyHashPairs = new KeyHashPairCollection { { 1024, 160 }, { 2048, 256 }, { 3072, 384 }/*, { 4096, 512 }*/ }.ToArray();
        #endregion

        #region Test Setup
        /// <summary>
        /// Set up a Global Discovery Server and Client instance and connect the session
        /// </summary>
        [OneTimeSetUp]
        protected void OneTimeSetUp()
        {
        }

        /// <summary>
        /// Clean up the Test PKI folder
        /// </summary>
        [OneTimeTearDown]
        protected void OneTimeTearDown()
        {
        }
        #endregion

        #region Test Methods
        /// <summary>
        /// Verify self signed app certs.
        /// </summary>
        [Theory]
        public void VerifySelfSignedAppCerts(
            KeyHashPair keyHashPair
            )
        {
            var appTestGenerator = new ApplicationTestDataGenerator(keyHashPair.KeySize);
            ApplicationTestData app = appTestGenerator.ApplicationTestSet(1).First();
            var cert = CertificateFactory.CreateCertificate(
                app.ApplicationUri, app.ApplicationName, app.Subject,
                app.DomainNames, keyHashPair.KeySize, DateTime.UtcNow,
                CertificateFactory.DefaultLifeTime, keyHashPair.HashSize);
            Assert.NotNull(cert);
            Assert.NotNull(cert.RawData);
            Assert.True(cert.HasPrivateKey);
            var plainCert = new X509Certificate2(cert.RawData);
            Assert.NotNull(plainCert);
            VerifyApplicationCert(app, plainCert);
            X509Utils.VerifySelfSigned(cert);
            X509Utils.VerifyRSAKeyPair(cert, cert);
        }

        /// <summary>
        /// Verify self signed OPC UA app certs.
        /// </summary>
        [Theory]
        public void VerifySignedAppCerts(
            KeyHashPair keyHashPair
            )
        {
            var subject = "CN=CA Test Cert";
            int pathLengthConstraint = -1;
            var caCert = CertificateFactory.CreateCertificate(
                null, null, subject,
                null, keyHashPair.KeySize,
                DateTime.UtcNow, 25 * 12,
                keyHashPair.HashSize,
                isCA: true,
                pathLengthConstraint: pathLengthConstraint);
            Assert.NotNull(caCert);
            Assert.NotNull(caCert.RawData);
            Assert.True(caCert.HasPrivateKey);

            var appTestGenerator = new ApplicationTestDataGenerator(keyHashPair.KeySize);
            ApplicationTestData app = appTestGenerator.ApplicationTestSet(1).First();
            var cert = CertificateFactory.CreateCertificate(
                app.ApplicationUri, app.ApplicationName, app.Subject,
                app.DomainNames, keyHashPair.KeySize, DateTime.UtcNow,
                CertificateFactory.DefaultLifeTime, keyHashPair.HashSize,
                issuerCAKeyCert: caCert
                );
            Assert.NotNull(cert);
            Assert.NotNull(cert.RawData);
            Assert.True(cert.HasPrivateKey);
            var plainCert = new X509Certificate2(cert.RawData);
            Assert.NotNull(plainCert);
            VerifyApplicationCert(app, plainCert, caCert);
            X509Utils.VerifyRSAKeyPair(cert, caCert);
        }


        /// <summary>
        /// Verify CA signed app certs.
        /// </summary>
        [Theory]
        public void VerifyCACerts(
            KeyHashPair keyHashPair
            )
        {
#if NETCOREAPP3_1
           // Assert.Ignore(keyHashPair.HashSize < 256);
#endif
            var subject = "CN=CA Test Cert";
            int pathLengthConstraint = (keyHashPair.KeySize / 512) - 3;
            var cert = CertificateFactory.CreateCertificate(
                null, null, subject,
                null, keyHashPair.KeySize,
                DateTime.UtcNow, 25 * 12,
                keyHashPair.HashSize,
                isCA: true,
                pathLengthConstraint: pathLengthConstraint);

            Assert.NotNull(cert);
            Assert.NotNull(cert.RawData);
            Assert.True(cert.HasPrivateKey);
            var plainCert = new X509Certificate2(cert.RawData);
            Assert.NotNull(plainCert);
            VerifyCACert(plainCert, subject, pathLengthConstraint);
            X509Utils.VerifySelfSigned(cert);
            X509Utils.VerifyRSAKeyPair(cert, cert);
        }

        // TODO: finalize
        /// <summary>
        /// Verify CRL for CA signed app certs.
        /// </summary>
        [Test]
        public void VerifyCrlCerts(
            )
        {
            KeyHashPair keyHashPair = new KeyHashPair(2048, 256);
            var subject = "CN=CA Test Cert";
            int pathLengthConstraint = 1;
            var issuerCertificate = CertificateFactory.CreateCertificate(
                null, null, subject,
                null, keyHashPair.KeySize,
                DateTime.UtcNow, 25 * 12,
                keyHashPair.HashSize,
                isCA: true,
                pathLengthConstraint: pathLengthConstraint);

            X509Certificate2Collection revokedCerts = new X509Certificate2Collection();
            for (int i = 0; i < 10; i++)
            {
                var cert = CertificateFactory.CreateCertificate(
                    null, null, $"CN=Test Cert {i}",
                    null, keyHashPair.KeySize,
                    DateTime.UtcNow, 2 * 12,
                    keyHashPair.HashSize,
                    isCA: false,
                    issuerCAKeyCert: issuerCertificate,
                    pathLengthConstraint: pathLengthConstraint);
                revokedCerts.Add(cert);
            }

            Assert.NotNull(issuerCertificate);
            Assert.NotNull(issuerCertificate.RawData);
            Assert.True(issuerCertificate.HasPrivateKey);
            using (var rsa = issuerCertificate.GetRSAPrivateKey())
            {
                Assert.NotNull(rsa);
            }

            var plainCert = new X509Certificate2(issuerCertificate.RawData);
            Assert.NotNull(plainCert);

            VerifyCACert(plainCert, subject, pathLengthConstraint);
            X509Utils.VerifySelfSigned(issuerCertificate);
            X509Utils.VerifyRSAKeyPair(issuerCertificate, issuerCertificate);
#if mist
            var crlLegacy = Opc.Ua.Legacy.CertificateFactory.RevokeCertificate(issuerCertificate, null, revokedCerts);
            Assert.NotNull(crlLegacy);
            File.WriteAllBytes("D:\\test1.crl", crlLegacy.RawData);
            crlLegacy.VerifySignature(issuerCertificate, true);
#endif
            var crl = CertificateFactory.RevokeCertificate(issuerCertificate, null, revokedCerts);
            File.WriteAllBytes("D:\\test2.crl", crl.RawData);
            Assert.NotNull(crl);

            var crl2 = CertificateFactory.RevokeCertificate(issuerCertificate, null, revokedCerts, DateTime.UtcNow, DateTime.MinValue);
            File.WriteAllBytes("D:\\test3.crl", crl.RawData);
            Assert.NotNull(crl2);
#if mist
            X509CrlParser parser = new X509CrlParser();
            X509Crl crlbcLegacy = parser.ReadCrl(crlLegacy.RawData);
            var crlVersionLegacy = CertificateFactory.GetCrlNumber(crlbcLegacy);
            X509Crl crlbc2 = parser.ReadCrl(crl.RawData);
            var crlVersion = CertificateFactory.GetCrlNumber(crlbc2);
#endif
            var newcrl = new X509CRL(crl.RawData);
            crl.VerifySignature(issuerCertificate, true);
            foreach (var cert in revokedCerts)
            {
                Assert.True(crl.IsRevoked(cert));
            }

            var newcrl2 = new X509CRL(crl.RawData);
            crl2.VerifySignature(issuerCertificate, true);
            foreach (var cert in revokedCerts)
            {
                Assert.True(crl2.IsRevoked(cert));
            }
        }
        #endregion

        #region Private Methods
        public static void VerifyApplicationCert(ApplicationTestData testApp, X509Certificate2 cert, X509Certificate2 issuerCert = null)
        {
            bool signedCert = issuerCert != null;
            if (issuerCert == null)
            {
                issuerCert = cert;
            }
            TestContext.Out.WriteLine($"{nameof(VerifyApplicationCert)}:");
            Assert.NotNull(cert);
            TestContext.Out.WriteLine(cert);
            Assert.False(cert.HasPrivateKey);
            Assert.True(X509Utils.CompareDistinguishedName(testApp.Subject, cert.Subject));
            Assert.True(X509Utils.CompareDistinguishedName(issuerCert.Subject, cert.Issuer));

            // test basic constraints
            X509BasicConstraintsExtension constraints = X509Extensions.FindExtension<X509BasicConstraintsExtension>(cert);
            Assert.NotNull(constraints);
            TestContext.Out.WriteLine(constraints.Format(true));
            Assert.True(constraints.Critical);
            if (signedCert)
            {
                Assert.False(constraints.CertificateAuthority);
                Assert.False(constraints.HasPathLengthConstraint);
            }
            else
            {
                Assert.True(constraints.CertificateAuthority);
                Assert.True(constraints.HasPathLengthConstraint);
                Assert.AreEqual(0, constraints.PathLengthConstraint);
            }

            // key usage
            X509KeyUsageExtension keyUsage = X509Extensions.FindExtension<X509KeyUsageExtension>(cert);
            Assert.NotNull(keyUsage);
            TestContext.Out.WriteLine(keyUsage.Format(true));
            Assert.True(keyUsage.Critical);
            Assert.True((keyUsage.KeyUsages & X509KeyUsageFlags.CrlSign) == 0);
            Assert.True((keyUsage.KeyUsages & X509KeyUsageFlags.DataEncipherment) == X509KeyUsageFlags.DataEncipherment);
            Assert.True((keyUsage.KeyUsages & X509KeyUsageFlags.DecipherOnly) == 0);
            Assert.True((keyUsage.KeyUsages & X509KeyUsageFlags.DigitalSignature) == X509KeyUsageFlags.DigitalSignature);
            Assert.True((keyUsage.KeyUsages & X509KeyUsageFlags.EncipherOnly) == 0);
            Assert.True((keyUsage.KeyUsages & X509KeyUsageFlags.KeyAgreement) == 0);
            Assert.True((keyUsage.KeyUsages & X509KeyUsageFlags.KeyCertSign) == (signedCert ? 0 : X509KeyUsageFlags.KeyCertSign));
            Assert.True((keyUsage.KeyUsages & X509KeyUsageFlags.KeyEncipherment) == X509KeyUsageFlags.KeyEncipherment);
            Assert.True((keyUsage.KeyUsages & X509KeyUsageFlags.NonRepudiation) == X509KeyUsageFlags.NonRepudiation);

            // enhanced key usage
            X509EnhancedKeyUsageExtension enhancedKeyUsage = X509Extensions.FindExtension<X509EnhancedKeyUsageExtension>(cert);
            Assert.NotNull(enhancedKeyUsage);
            TestContext.Out.WriteLine(enhancedKeyUsage.Format(true));
            Assert.True(enhancedKeyUsage.Critical);

            // test for authority key
            X509AuthorityKeyIdentifierExtension authority = X509Extensions.FindExtension<X509AuthorityKeyIdentifierExtension>(cert);
            Assert.NotNull(authority);
            TestContext.Out.WriteLine(authority.Format(true));
            Assert.NotNull(authority.SerialNumber);
            Assert.NotNull(authority.KeyIdentifier);
            Assert.NotNull(authority.Issuer);
            if (issuerCert == null)
            {
                Assert.AreEqual(cert.SubjectName.RawData, authority.Issuer.RawData);
                Assert.True(X509Utils.CompareDistinguishedName(cert.SubjectName.Name, authority.Issuer.Name), $"{cert.SubjectName.Name} != {authority.Issuer.Name}");
            }
            else
            {
                Assert.AreEqual(issuerCert.SubjectName.RawData, authority.Issuer.RawData);
                Assert.True(X509Utils.CompareDistinguishedName(issuerCert.SubjectName.Name, authority.Issuer.Name), $"{cert.SubjectName.Name} != {authority.Issuer.Name}");
            }

            // verify authority key in signed cert
            X509SubjectKeyIdentifierExtension subjectKeyId = X509Extensions.FindExtension<X509SubjectKeyIdentifierExtension>(cert);
            TestContext.Out.WriteLine(subjectKeyId.Format(true));
            if (signedCert)
            {
                var caCertSubjectKeyId = X509Extensions.FindExtension<X509SubjectKeyIdentifierExtension>(issuerCert);
                Assert.NotNull(caCertSubjectKeyId);
                Assert.AreEqual(caCertSubjectKeyId.SubjectKeyIdentifier, authority.KeyIdentifier);
            }
            else
            {
                Assert.AreEqual(subjectKeyId.SubjectKeyIdentifier, authority.KeyIdentifier);
            }
            Assert.AreEqual(issuerCert.GetSerialNumber(), authority.GetSerialNumber());
            Assert.AreEqual(issuerCert.SerialNumber, authority.SerialNumber);

            X509SubjectAltNameExtension subjectAlternateName = X509Extensions.FindExtension<X509SubjectAltNameExtension>(cert);
            Assert.NotNull(subjectAlternateName);
            TestContext.Out.WriteLine(subjectAlternateName.Format(true));
            Assert.False(subjectAlternateName.Critical);
            var domainNames = X509Utils.GetDomainsFromCertficate(cert);
            foreach (var domainName in testApp.DomainNames)
            {
                Assert.True(domainNames.Contains(domainName, StringComparer.OrdinalIgnoreCase));
            }
            Assert.True(subjectAlternateName.Uris.Count == 1);
            var applicationUri = X509Utils.GetApplicationUriFromCertificate(cert);
            TestContext.Out.WriteLine("ApplicationUri: ");
            TestContext.Out.WriteLine(applicationUri);
            Assert.AreEqual(testApp.ApplicationUri, applicationUri);
        }

        public static void VerifyCACert(X509Certificate2 cert, string subject, int pathLengthConstraint)
        {
            TestContext.Out.WriteLine($"{nameof(VerifyCACert)}:");

            Assert.NotNull(cert);
            TestContext.Out.WriteLine(cert);
            Assert.False(cert.HasPrivateKey);
            Assert.True(X509Utils.CompareDistinguishedName(subject, cert.Subject));
            Assert.True(X509Utils.CompareDistinguishedName(subject, cert.Issuer));

            // test basic constraints
            var constraints = X509Extensions.FindExtension<X509BasicConstraintsExtension>(cert);
            Assert.NotNull(constraints);
            TestContext.Out.WriteLine(constraints.Format(true));
            Assert.True(constraints.Critical);
            Assert.True(constraints.CertificateAuthority);
            if (pathLengthConstraint < 0)
            {
                Assert.False(constraints.HasPathLengthConstraint);
            }
            else
            {
                Assert.True(constraints.HasPathLengthConstraint);
                Assert.AreEqual(pathLengthConstraint, constraints.PathLengthConstraint);
            }

            // key usage
            var keyUsage = X509Extensions.FindExtension<X509KeyUsageExtension>(cert);
            Assert.NotNull(keyUsage);
            TestContext.Out.WriteLine(keyUsage.Format(true));
            Assert.True(keyUsage.Critical);
            Assert.True((keyUsage.KeyUsages & X509KeyUsageFlags.CrlSign) == X509KeyUsageFlags.CrlSign);
            Assert.True((keyUsage.KeyUsages & X509KeyUsageFlags.DataEncipherment) == 0);
            Assert.True((keyUsage.KeyUsages & X509KeyUsageFlags.DecipherOnly) == 0);
            Assert.True((keyUsage.KeyUsages & X509KeyUsageFlags.DigitalSignature) == X509KeyUsageFlags.DigitalSignature);
            Assert.True((keyUsage.KeyUsages & X509KeyUsageFlags.EncipherOnly) == 0);
            Assert.True((keyUsage.KeyUsages & X509KeyUsageFlags.KeyAgreement) == 0);
            Assert.True((keyUsage.KeyUsages & X509KeyUsageFlags.KeyCertSign) == X509KeyUsageFlags.KeyCertSign);
            Assert.True((keyUsage.KeyUsages & X509KeyUsageFlags.KeyEncipherment) == 0);
            Assert.True((keyUsage.KeyUsages & X509KeyUsageFlags.NonRepudiation) == 0);

            // enhanced key usage
            X509EnhancedKeyUsageExtension enhancedKeyUsage = X509Extensions.FindExtension<X509EnhancedKeyUsageExtension>(cert);
            Assert.Null(enhancedKeyUsage);

            // test for authority key
            X509AuthorityKeyIdentifierExtension authority = X509Extensions.FindExtension<X509AuthorityKeyIdentifierExtension>(cert);
            Assert.NotNull(authority);
            TestContext.Out.WriteLine(authority.Format(true));
            Assert.NotNull(authority.SerialNumber);
            Assert.NotNull(authority.GetSerialNumber());
            Assert.NotNull(authority.KeyIdentifier);
            Assert.NotNull(authority.Issuer);
            Assert.NotNull(authority.ToString());
            Assert.AreEqual(authority.SerialNumber, Utils.ToHexString(authority.GetSerialNumber(), true));

            // verify authority key in signed cert
            X509SubjectKeyIdentifierExtension subjectKeyId = X509Extensions.FindExtension<X509SubjectKeyIdentifierExtension>(cert);
            TestContext.Out.WriteLine(subjectKeyId.Format(true));
            Assert.AreEqual(subjectKeyId.SubjectKeyIdentifier, authority.KeyIdentifier);
            Assert.AreEqual(cert.SerialNumber, authority.SerialNumber);
            Assert.AreEqual(cert.GetSerialNumber(), authority.GetSerialNumber());

            X509SubjectAltNameExtension subjectAlternateName = X509Extensions.FindExtension<X509SubjectAltNameExtension>(cert);
            Assert.Null(subjectAlternateName);
        }
        #endregion

        #region Private Fields
        #endregion
    }

}
