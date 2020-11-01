/* Copyright (c) 1996-2019 The OPC Foundation. All rights reserved.
   The source code in this file is covered under a dual-license scenario:
     - RCL: for OPC Foundation members in good-standing
     - GPL V2: everybody else
   RCL license terms accompanied with this source code. See http://opcfoundation.org/License/RCL/1.00/
   GNU General Public License as published by the Free Software Foundation;
   version 2 of the License are accompanied with this source code. See http://opcfoundation.org/License/GPLv2
   This source code is distributed in the hope that it will be useful,
   but WITHOUT ANY WARRANTY; without even the implied warranty of
   MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.
*/

#if NETSTANDARD2_1
using System;
using System.Collections;
using System.Collections.Generic;
using System.Formats.Asn1;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;
using Org.BouncyCastle.Asn1;
using Org.BouncyCastle.Asn1.Pkcs;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Operators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Prng;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.OpenSsl;
using Org.BouncyCastle.Pkcs;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.X509;
using Org.BouncyCastle.X509.Extension;

namespace Opc.Ua
{
    internal static class SignatureBuilder
    {
        /// <summary>
        /// Adds a signature to encoded data in ASN format.
        /// </summary>
        /// <param name="encodedData">ASN encoded data.</param>
        /// <param name="signature">signature of the encoded data.</param>
        /// <param name="hashAlgorithmName"></param>
        /// <returns>X509 ASN format of EncodedData+SignatureOID+Signature bytes.</returns>
        public static byte[] AddSignature(byte[] encodedData, byte[] signature, HashAlgorithmName hashAlgorithmName)
        {
            AsnWriter writer = new AsnWriter(AsnEncodingRules.DER);

            var tag = Asn1Tag.Sequence;
            writer.PushSequence(tag);

            // write Tbs encoded data
            writer.WriteEncodedValue(encodedData);

            // Signature Algorithm Identifier
            writer.PushSequence();
            string signatureAlgorithm = CrlBuilder.GetRSAOid(hashAlgorithmName);
            writer.WriteObjectIdentifier(signatureAlgorithm);
            writer.WriteNull();
            writer.PopSequence();

            // Add signature
            writer.WriteBitString(signature);

            writer.PopSequence(tag);

            return writer.Encode();
        }

        static byte[] EncodeECDSASignatureToASNFormat(byte[] signature)
        {
            /*
             * Encode from ieee signature format to ASN1 DER encoded signature format for ecdsa certificates.
             * ECDSA-Sig-Value ::= SEQUENCE { r INTEGER, s INTEGER }
             * https://www.ietf.org/rfc/rfc5480.txt
             */
            AsnWriter writer = new AsnWriter(AsnEncodingRules.DER);
            var tag = Asn1Tag.Sequence;
            writer.PushSequence(tag);

            int segmentLength = signature.Length / 2;
            writer.WriteIntegerUnsigned(new ReadOnlySpan<byte>(signature, 0, segmentLength));
            writer.WriteIntegerUnsigned(new ReadOnlySpan<byte>(signature, segmentLength, segmentLength));

            writer.PopSequence(tag);

            return writer.Encode();
        }
    }


    /// <summary>
    /// Secure .Net Core Random Number generator wrapper for Bounce Castle.
    /// Creates an instance of RNGCryptoServiceProvider or an OpenSSL based version on other OS.
    /// </summary>
    public class CertificateFactoryRandomGenerator : IRandomGenerator, IDisposable
    {
        RandomNumberGenerator m_prg;

        /// <summary>
        /// Creates an instance of a crypthographic secure random number generator.
        /// </summary>
        public CertificateFactoryRandomGenerator()
        {
            m_prg = RandomNumberGenerator.Create();
        }

        /// <summary>
        /// Dispose the random number generator.
        /// </summary>
        public void Dispose()
        {
            m_prg.Dispose();
        }

        /// <summary>Add more seed material to the generator. Not needed here.</summary>
        public void AddSeedMaterial(byte[] seed) { }

        /// <summary>Add more seed material to the generator. Not needed here.</summary>
        public void AddSeedMaterial(long seed) { }

        /// <summary>
        /// Fills an array of bytes with a cryptographically strong
        /// random sequence of values.
        /// </summary>
        /// <param name="bytes">Array to be filled.</param>
        public void NextBytes(byte[] bytes)
        {
            m_prg.GetBytes(bytes);
        }

        /// <summary>
        /// Fills an array of bytes with a cryptographically strong
        /// random sequence of values.
        /// </summary>
        /// <param name="bytes">Array to receive bytes.</param>
        /// <param name="start">Index to start filling at.</param>
        /// <param name="len">Length of segment to fill.</param>
        public void NextBytes(byte[] bytes, int start, int len)
        {
            byte[] temp = new byte[len];
            m_prg.GetBytes(temp);
            Array.Copy(temp, 0, bytes, start, len);
        }
    }

    /// <summary>
    /// Always use this converter class to create a X509Name object 
    /// from X509Certificate subject.
    /// </summary>
    /// <remarks>
    /// Handles subtle differences in the string representation
    /// of the .NET and the Bouncy Castle implementation.
    /// </remarks>
    public class CertificateFactoryX509Name : X509Name
    {

        /// <summary>
        /// Create the X509Name from a distinguished name.
        /// </summary>
        /// <param name="distinguishedName">The distinguished name.</param>
        public CertificateFactoryX509Name(string distinguishedName) :
            base(true, ConvertToX509Name(distinguishedName))
        {
        }

        /// <summary>
        /// Create the X509Name from a distinguished name.
        /// </summary>
        /// <param name="reverse">Reverse the order of the names.</param>
        /// <param name="distinguishedName">The distinguished name.</param>
        public CertificateFactoryX509Name(bool reverse, string distinguishedName) :
            base(reverse, ConvertToX509Name(distinguishedName))
        {
        }

        private static string ConvertToX509Name(string distinguishedName)
        {
            // convert from X509Certificate to bouncy castle DN entries
            return distinguishedName.Replace("S=", "ST=");
        }
    }

    /// <summary>
    /// Creates certificates.
    /// </summary>
    public static class CertificateFactory
    {
        #region Public Constants
        /// <summary>
        /// The default key size for RSA certificates in bits.
        /// </summary>
        /// <remarks>
        /// Supported values are 1024(deprecated), 2048, 3072 or 4096.
        /// </remarks>
        public static readonly ushort DefaultKeySize = 2048;
        /// <summary>
        /// The default hash size for RSA certificates in bits.
        /// </summary>
        /// <remarks>
        /// Supported values are 160 for SHA-1(deprecated) or 256, 384 and 512 for SHA-2.
        /// </remarks>
        public static readonly ushort DefaultHashSize = 256;
        /// <summary>
        /// The default lifetime of certificates in months.
        /// </summary>
        public static readonly ushort DefaultLifeTime = 12;
        #endregion

        #region Public Methods
        /// <summary>
        /// Creates a certificate from a buffer with DER encoded certificate.
        /// </summary>
        /// <param name="encodedData">The encoded data.</param>
        /// <param name="useCache">if set to <c>true</c> the copy of the certificate in the cache is used.</param>
        /// <returns>The certificate.</returns>
        public static X509Certificate2 Create(byte[] encodedData, bool useCache)
        {
            if (useCache)
            {
                return Load(new X509Certificate2(encodedData), false);
            }
            return new X509Certificate2(encodedData);
        }

        /// <summary>
        /// Loads the cached version of a certificate.
        /// </summary>
        /// <param name="certificate">The certificate to load.</param>
        /// <param name="ensurePrivateKeyAccessible">If true a key container is created for a certificate that must be deleted by calling Cleanup.</param>
        /// <returns>The cached certificate.</returns>
        /// <remarks>
        /// This function is necessary because all private keys used for cryptography 
        /// operations must be in a key container. 
        /// Private keys stored in a PFX file have no key container by default.
        /// </remarks>
        public static X509Certificate2 Load(X509Certificate2 certificate, bool ensurePrivateKeyAccessible)
        {
            if (certificate == null)
            {
                return null;
            }

            lock (m_certificates)
            {
                X509Certificate2 cachedCertificate = null;

                // check for existing cached certificate.
                if (m_certificates.TryGetValue(certificate.Thumbprint, out cachedCertificate))
                {
                    return cachedCertificate;
                }

                // nothing more to do if no private key or dont care about accessibility.
                if (!certificate.HasPrivateKey || !ensurePrivateKeyAccessible)
                {
                    return certificate;
                }

                // update the cache.
                m_certificates[certificate.Thumbprint] = certificate;

                if (m_certificates.Count > 100)
                {
                    Utils.Trace("WARNING - Process certificate cache has {0} certificates in it.", m_certificates.Count);
                }

                // save the key container so it can be deleted later.
               // m_temporaryKeyContainers.Add(certificate);
            }

            return certificate;
        }
        /// <summary>
        /// Creates a self signed application instance certificate.
        /// </summary>
        /// <param name="storeType">Type of certificate store (Directory) <see cref="CertificateStoreType"/>.</param>
        /// <param name="storePath">The store path (syntax depends on storeType).</param>
        /// <param name="password">The password to use to protect the certificate.</param>
        /// <param name="applicationUri">The application uri (created if not specified).</param>
        /// <param name="applicationName">Name of the application (optional if subjectName is specified).</param>
        /// <param name="subjectName">The subject used to create the certificate (optional if applicationName is specified).</param>
        /// <param name="domainNames">The domain names that can be used to access the server machine (defaults to local computer name if not specified).</param>
        /// <param name="keySize">Size of the key (1024, 2048 or 4096).</param>
        /// <param name="startTime">The start time.</param>
        /// <param name="lifetimeInMonths">The lifetime of the key in months.</param>
        /// <param name="hashSizeInBits">The hash size in bits.</param>
        /// <param name="isCA">if set to <c>true</c> then a CA certificate is created.</param>
        /// <param name="issuerCAKeyCert">The CA cert with the CA private key.</param>
        /// <param name="publicKey">The public key if no new keypair is created.</param>
        /// <param name="pathLengthConstraint">The path length constraint for CA certs.</param>
        /// <param name="extensionUrl"></param>
        /// <returns>The certificate with a private key.</returns>
        public static X509Certificate2 CreateCertificate(
            string storeType,
            string storePath,
            string password,
            string applicationUri,
            string applicationName,
            string subjectName,
            IList<String> domainNames,
            ushort keySize,
            DateTime startTime,
            ushort lifetimeInMonths,
            ushort hashSizeInBits,
            bool isCA = false,
            X509Certificate2 issuerCAKeyCert = null,
            byte[] publicKey = null,
            int pathLengthConstraint = 0,
            string extensionUrl = null)
        {
            RSA rsaPublicKey = null;
            if (publicKey != null)
            {
                try
                {
                    var asymmetricKeyParameter = PublicKeyFactory.CreateKey(publicKey);
                    var rsaKeyParameters = asymmetricKeyParameter as RsaKeyParameters;
                    var parameters = new RSAParameters {
                        Exponent = rsaKeyParameters.Exponent.ToByteArrayUnsigned(),
                        Modulus = rsaKeyParameters.Modulus.ToByteArrayUnsigned()
                    };
                    rsaPublicKey = RSA.Create();
                    rsaPublicKey.ImportParameters(parameters);
                }
                catch
                {
                    rsaPublicKey = null;
                }
            }

            if (rsaPublicKey != null)
            {
                if (rsaPublicKey.KeySize != keySize)
                {
                    throw new NotSupportedException(String.Format("Public key size {0} does not match expected key size {1}", rsaPublicKey.KeySize, keySize));
                }
            }

            int serialNumberLength = 20;

            // new serial number
            byte[] serialNumber = new byte[serialNumberLength];
            RandomNumberGenerator.Fill(serialNumber);
            serialNumber[0] &= 0x7F;

            // set default values.
            X500DistinguishedName subjectDN = SetSuitableDefaults(
                ref applicationUri,
                ref applicationName,
                ref subjectName,
                ref domainNames,
                ref keySize,
                ref lifetimeInMonths);

            DateTime notBefore = startTime;
            DateTime notAfter = startTime + TimeSpan.FromDays(lifetimeInMonths * 30);

            RSA rsaKeyPair = null;
            if (rsaPublicKey == null)
            {
                rsaKeyPair = RSA.Create(keySize);
                rsaPublicKey = rsaKeyPair;
            }
            var request = new CertificateRequest(subjectDN, rsaPublicKey, GetRSAHashAlgorithmName(hashSizeInBits), RSASignaturePadding.Pkcs1);

            BasicConstraints basicConstraints = new BasicConstraints(isCA);
            if (isCA && pathLengthConstraint >= 0)
            {
                basicConstraints = new BasicConstraints(pathLengthConstraint);
            }
            else if (!isCA && issuerCAKeyCert == null)
            {   // self-signed
                basicConstraints = new BasicConstraints(0);
            }


            // Basic constraints
            if (!isCA && issuerCAKeyCert == null)
            {
                // self signed
                request.CertificateExtensions.Add(
                    new X509BasicConstraintsExtension(true, true, 0, true));
            }
            else if (isCA && pathLengthConstraint >= 0)
            {
                // CA with constraints
                request.CertificateExtensions.Add(
                    new X509BasicConstraintsExtension(true, true, pathLengthConstraint, true));
            }
            else
            {
                request.CertificateExtensions.Add(
                    new X509BasicConstraintsExtension(isCA, false, 0, true));
            }

            // Subject Key Identifier
            var ski = new X509SubjectKeyIdentifierExtension(
                request.PublicKey,
                X509SubjectKeyIdentifierHashAlgorithm.Sha1,
                false);
            request.CertificateExtensions.Add(ski);

            // Authority Key Identifier
            if (issuerCAKeyCert != null)
            {
                request.CertificateExtensions.Add(BuildAuthorityKeyIdentifier(issuerCAKeyCert));
            }
            else
            {
                request.CertificateExtensions.Add(BuildAuthorityKeyIdentifier(subjectDN, serialNumber.Reverse().ToArray(), ski));
            }

            if (isCA)
            {
                request.CertificateExtensions.Add(
                    new X509KeyUsageExtension(
                        X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign,
                        true));
                if (extensionUrl != null)
                {
                    // add CRL endpoint, if available
                    request.CertificateExtensions.Add(
                        BuildX509CRLDistributionPoints(PatchExtensionUrl(extensionUrl, serialNumber))
                        );
                }
            }
            else
            {
                // Key Usage
                X509KeyUsageFlags defaultFlags =
                    X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.DataEncipherment |
                        X509KeyUsageFlags.NonRepudiation | X509KeyUsageFlags.KeyEncipherment;
                if (issuerCAKeyCert == null)
                {
                    // self signed case
                    defaultFlags |= X509KeyUsageFlags.KeyCertSign;
                }
                request.CertificateExtensions.Add(
                    new X509KeyUsageExtension(defaultFlags, true));

                // Enhanced key usage
                request.CertificateExtensions.Add(
                    new X509EnhancedKeyUsageExtension(
                        new OidCollection {
                        new Oid("1.3.6.1.5.5.7.3.1"),
                        new Oid("1.3.6.1.5.5.7.3.2") }, true));

                // Subject Alternative Name
                var subjectAltName = BuildSubjectAlternativeName(applicationUri, domainNames);
                request.CertificateExtensions.Add(new System.Security.Cryptography.X509Certificates.X509Extension(subjectAltName, false));

                if (issuerCAKeyCert != null &&
                    extensionUrl != null)
                {   // add Authority Information Access, if available
                    request.CertificateExtensions.Add(
                        BuildX509AuthorityInformationAccess(new string[] { PatchExtensionUrl(extensionUrl, issuerCAKeyCert.SerialNumber) })
                        );
                }
            }

            if (issuerCAKeyCert != null)
            {
                if (notAfter > issuerCAKeyCert.NotAfter)
                {
                    notAfter = issuerCAKeyCert.NotAfter;
                }
                if (notBefore < issuerCAKeyCert.NotBefore)
                {
                    notBefore = issuerCAKeyCert.NotBefore;
                }
            }

            X509Certificate2 certificate = null;
            X509Certificate2 signedCert;
            if (issuerCAKeyCert != null)
            {
                var issuerSubjectName = issuerCAKeyCert != null ? issuerCAKeyCert.SubjectName : subjectDN;
                using (RSA rsa = issuerCAKeyCert.GetRSAPrivateKey())
                {
                    var generator = X509SignatureGenerator.CreateForRSA(rsa, RSASignaturePadding.Pkcs1);
                    signedCert = request.Create(
                        issuerCAKeyCert,
                        notBefore,
                        notAfter,
                        serialNumber
                        );
                }
            }
            else
            {
                var generator = X509SignatureGenerator.CreateForRSA(rsaPublicKey, RSASignaturePadding.Pkcs1);
                signedCert = request.Create(
                    subjectDN,
                    generator,
                    notBefore,
                    notAfter,
                    serialNumber
                    );
            }

            // convert to X509Certificate2
            if (rsaKeyPair == null)
            {
                // create the cert without the private key
                certificate = signedCert;
            }
            else
            {
                // note: this cert has a private key!
                certificate = signedCert.CopyWithPrivateKey(rsaKeyPair);
            }

            // add cert to the store.
            if (!String.IsNullOrEmpty(storePath) && !String.IsNullOrEmpty(storeType))
            {
                using (ICertificateStore store = CertificateStoreIdentifier.CreateStore(storeType))
                {
                    if (store == null)
                    {
                        throw new ArgumentException("Invalid store type");
                    }

                    store.Open(storePath);
                    store.Add(certificate, password).Wait();
                    store.Close();
                }
            }

            return certificate;
        }

        /// <summary>
        /// Creates a certificate from a PKCS #12 store with a private key.
        /// </summary>
        /// <param name="rawData">The raw PKCS #12 store data.</param>
        /// <param name="password">The password to use to access the store.</param>
        /// <returns>The certificate with a private key.</returns>
        public static X509Certificate2 CreateCertificateFromPKCS12(
            byte[] rawData,
            string password
            )
        {
            Exception ex = null;
            int flagsRetryCounter = 0;
            X509Certificate2 certificate = null;

            // We need to try MachineKeySet first as UserKeySet in combination with PersistKeySet hangs ASP.Net WebApps on Azure
            X509KeyStorageFlags[] storageFlags = {
                X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.MachineKeySet,
                X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.UserKeySet
            };

            // try some combinations of storage flags, support is platform dependent
            while (certificate == null &&
                flagsRetryCounter < storageFlags.Length)
            {
                try
                {
                    // merge first cert with private key into X509Certificate2
                    certificate = new X509Certificate2(
                        rawData,
                        password ?? String.Empty,
                        storageFlags[flagsRetryCounter]);
                    // can we really access the private key?
                    if (VerifyRSAKeyPair(certificate, certificate, true))
                    {
                        return certificate;
                    }
                }
                catch (Exception e)
                {
                    ex = e;
                    certificate?.Dispose();
                    certificate = null;
                }
                flagsRetryCounter++;
            }

            if (certificate == null)
            {
                throw new NotSupportedException("Creating X509Certificate from PKCS #12 store failed", ex);
            }

            return certificate;
        }

        /// <summary>
        /// Revoke the CA signed certificate. 
        /// The issuer CA public key, the private key and the crl reside in the storepath.
        /// The CRL number is increased by one and existing CRL for the issuer are deleted from the store.
        /// </summary>
        public static async Task<X509CRL> RevokeCertificateAsync(
            string storePath,
            X509Certificate2 certificate,
            string issuerKeyFilePassword = null
            )
        {
            X509CRL updatedCRL = null;
            try
            {
                string subjectName = certificate.IssuerName.Name;
                string keyId = null;
                string serialNumber = null;

                // caller may want to create empty CRL using the CA cert itself
                bool isCACert = IsCertificateAuthority(certificate);

                // find the authority key identifier.
                X509AuthorityKeyIdentifierExtension authority = FindAuthorityKeyIdentifier(certificate);

                if (authority != null)
                {
                    keyId = authority.KeyId;
                    serialNumber = authority.SerialNumber;
                }
                else
                {
                    throw new ArgumentException("Certificate does not contain an Authority Key");
                }

                if (!isCACert)
                {
                    if (serialNumber == certificate.SerialNumber ||
                        Utils.CompareDistinguishedName(certificate.Subject, certificate.Issuer))
                    {
                        throw new ServiceResultException(StatusCodes.BadCertificateInvalid, "Cannot revoke self signed certificates");
                    }
                }

                X509Certificate2 certCA = null;
                using (ICertificateStore store = CertificateStoreIdentifier.OpenStore(storePath))
                {
                    if (store == null)
                    {
                        throw new ArgumentException("Invalid store path/type");
                    }
                    certCA = await FindIssuerCABySerialNumberAsync(store, certificate.Issuer, serialNumber);

                    if (certCA == null)
                    {
                        throw new ServiceResultException(StatusCodes.BadCertificateInvalid, "Cannot find issuer certificate in store.");
                    }

                    if (!certCA.HasPrivateKey)
                    {
                        throw new ServiceResultException(StatusCodes.BadCertificateInvalid, "Issuer certificate has no private key, cannot revoke certificate.");
                    }

                    CertificateIdentifier certCAIdentifier = new CertificateIdentifier(certCA);
                    certCAIdentifier.StorePath = storePath;
                    certCAIdentifier.StoreType = CertificateStoreIdentifier.DetermineStoreType(storePath);
                    X509Certificate2 certCAWithPrivateKey = await certCAIdentifier.LoadPrivateKey(issuerKeyFilePassword);

                    if (certCAWithPrivateKey == null)
                    {
                        throw new ServiceResultException(StatusCodes.BadCertificateInvalid, "Failed to load issuer private key. Is the password correct?");
                    }

                    List<X509CRL> certCACrl = store.EnumerateCRLs(certCA, false);

                    var certificateCollection = new X509Certificate2Collection() { };
                    if (!isCACert)
                    {
                        certificateCollection.Add(certificate);
                    }
                    updatedCRL = RevokeCertificate(certCAWithPrivateKey, certCACrl, certificateCollection);

                    store.AddCRL(updatedCRL);

                    // delete outdated CRLs from store
                    foreach (X509CRL caCrl in certCACrl)
                    {
                        store.DeleteCRL(caCrl);
                    }
                    store.Close();
                }
            }
            catch (Exception)
            {
                throw;
            }
            return updatedCRL;
        }

        /// <summary>
        /// Revoke the certificate. 
        /// The CRL number is increased by one and the new CRL is returned.
        /// </summary>
        public static X509CRL RevokeCertificate(
            X509Certificate2 issuerCertificate,
            List<X509CRL> issuerCrls,
            X509Certificate2Collection revokedCertificates
            )
        {
            return RevokeCertificate(issuerCertificate, issuerCrls, revokedCertificates,
                DateTime.UtcNow, DateTime.UtcNow.AddMonths(12));
        }

        /// <summary>
        /// Revoke the certificate. 
        /// The CRL number is increased by one and the new CRL is returned.
        /// </summary>
        public static X509CRL RevokeCertificate(
            X509Certificate2 issuerCertificate,
            List<X509CRL> issuerCrls,
            X509Certificate2Collection revokedCertificates,
            DateTime thisUpdate,
            DateTime nextUpdate
            )
        {
            if (!issuerCertificate.HasPrivateKey)
            {
                throw new ServiceResultException(StatusCodes.BadCertificateInvalid, "Issuer certificate has no private key, cannot revoke certificate.");
            }

            System.Numerics.BigInteger crlSerialNumber;
            IList<string> serialNumbers = new List<string>();

            // merge all existing revocation list
            if (issuerCrls != null)
            {
                X509CrlParser parser = new X509CrlParser();
                foreach (X509CRL issuerCrl in issuerCrls)
                {

                    X509Crl crl = parser.ReadCrl(issuerCrl.RawData);
                    // TODO: add serialnumbers
                    var crlVersion = new System.Numerics.BigInteger(GetCrlNumber(crl).ToByteArrayUnsigned());
                    if (crlVersion > crlSerialNumber)
                    {
                        crlSerialNumber = crlVersion;
                    }
                }
            }

            // add existing serial numbers
            if (revokedCertificates != null)
            {
                foreach (var cert in revokedCertificates)
                {
                    serialNumbers.Add(cert.SerialNumber);
                }
            }

            var hashAlgorithmName = HashAlgorithmName.SHA256;
            CrlBuilder cRLBuilder = new CrlBuilder(issuerCertificate.SubjectName, serialNumbers.ToArray(), hashAlgorithmName);
            cRLBuilder.NextUpdate = nextUpdate;
            cRLBuilder.CrlExtensions.Add(BuildAuthorityKeyIdentifier(issuerCertificate));
            cRLBuilder.CrlExtensions.Add(BuildCRLNumber(123));
            byte[] crlRawData = cRLBuilder.GetEncoded();
            using (RSA rsa = issuerCertificate.GetRSAPrivateKey())
            {
                var generator = X509SignatureGenerator.CreateForRSA(rsa, RSASignaturePadding.Pkcs1);
                byte[] signature = generator.SignData(crlRawData, hashAlgorithmName);
                byte[] crlWithSignature = SignatureBuilder.AddSignature(crlRawData, signature, hashAlgorithmName);
                return new X509CRL(crlWithSignature);
            }
        }


        /// <summary>
        /// Creates a certificate signing request from an existing certificate.
        /// </summary>
        public static byte[] CreateSigningRequest(
            X509Certificate2 certificate,
            IList<String> domainNames = null
            )
        {
            using (var cfrg = new CertificateFactoryRandomGenerator())
            {
                SecureRandom random = new SecureRandom(cfrg);

                // try to get signing/private key from certificate passed in
                AsymmetricKeyParameter signingKey = GetPrivateKeyParameter(certificate);
                RsaKeyParameters publicKey = GetPublicKeyParameter(certificate);

                ISignatureFactory signatureFactory =
                    new Asn1SignatureFactory(GetRSAHashAlgorithm(DefaultHashSize), signingKey, random);

                Asn1Set attributes = null;
                X509SubjectAltNameExtension alternateName = null;
                foreach (System.Security.Cryptography.X509Certificates.X509Extension extension in certificate.Extensions)
                {
                    if (extension.Oid.Value == X509SubjectAltNameExtension.SubjectAltNameOid || extension.Oid.Value == X509SubjectAltNameExtension.SubjectAltName2Oid)
                    {
                        alternateName = new X509SubjectAltNameExtension(extension, extension.Critical);
                        break;
                    }
                }

                domainNames = domainNames ?? new List<String>();
                if (alternateName != null)
                {
                    foreach (var name in alternateName.DomainNames)
                    {
                        if (!domainNames.Any(s => s.Equals(name, StringComparison.OrdinalIgnoreCase)))
                        {
                            domainNames.Add(name);
                        }
                    }
                    foreach (var ipAddress in alternateName.IPAddresses)
                    {
                        if (!domainNames.Any(s => s.Equals(ipAddress, StringComparison.OrdinalIgnoreCase)))
                        {
                            domainNames.Add(ipAddress);
                        }
                    }
                }

                // build CSR extensions
                List<GeneralName> generalNames = new List<GeneralName>();

                string applicationUri = Utils.GetApplicationUriFromCertificate(certificate);
                if (applicationUri != null)
                {
                    generalNames.Add(new GeneralName(GeneralName.UniformResourceIdentifier, applicationUri));
                }

                if (domainNames.Count > 0)
                {
                    generalNames.AddRange(CreateSubjectAlternateNameDomains(domainNames));
                }

                if (generalNames.Count > 0)
                {
                    IList oids = new ArrayList();
                    IList values = new ArrayList();
                    oids.Add(X509Extensions.SubjectAlternativeName);
                    values.Add(new Org.BouncyCastle.Asn1.X509.X509Extension(false,
                        new DerOctetString(new GeneralNames(generalNames.ToArray()).GetDerEncoded())));
                    AttributePkcs attribute = new AttributePkcs(PkcsObjectIdentifiers.Pkcs9AtExtensionRequest,
                        new DerSet(new X509Extensions(oids, values)));
                    attributes = new DerSet(attribute);
                }

                Pkcs10CertificationRequest pkcs10CertificationRequest = new Pkcs10CertificationRequest(
                    signatureFactory,
                    new CertificateFactoryX509Name(false, certificate.Subject),
                    publicKey,
                    attributes);

                return pkcs10CertificationRequest.GetEncoded();
            }
        }

        /// <summary>
        /// Create a X509Certificate2 with a private key by combining 
        /// the new certificate with a private key from an existing certificate
        /// </summary>
        public static X509Certificate2 CreateCertificateWithPrivateKey(
            X509Certificate2 certificate,
            X509Certificate2 certificateWithPrivateKey)
        {
            if (!certificateWithPrivateKey.HasPrivateKey)
            {
                throw new NotSupportedException("Need a certificate with a private key.");
            }

            if (!VerifyRSAKeyPair(certificate, certificateWithPrivateKey))
            {
                throw new NotSupportedException("The public and the private key pair doesn't match.");
            }

            return certificate.CopyWithPrivateKey(certificateWithPrivateKey.GetRSAPrivateKey());
        }

        /// <summary>
        /// Create a X509Certificate2 with a private key by combining 
        /// the certificate with a private key from a PEM stream
        /// </summary>
        public static X509Certificate2 CreateCertificateWithPEMPrivateKey(
            X509Certificate2 certificate,
            byte[] pemDataBlob,
            string password = null)
        {
            AsymmetricKeyParameter privateKey = null;
            PemReader pemReader;
            using (StreamReader pemStreamReader = new StreamReader(new MemoryStream(pemDataBlob), Encoding.UTF8, true))
            {
                if (String.IsNullOrEmpty(password))
                {
                    pemReader = new PemReader(pemStreamReader);
                }
                else
                {
                    Password pwFinder = new Password(password.ToCharArray());
                    pemReader = new PemReader(pemStreamReader, pwFinder);
                }
                try
                {
                    // find the private key in the PEM blob
                    var pemObject = pemReader.ReadObject();
                    while (pemObject != null)
                    {
                        privateKey = pemObject as RsaPrivateCrtKeyParameters;
                        if (privateKey != null)
                        {
                            break;
                        }

                        AsymmetricCipherKeyPair keypair = pemObject as AsymmetricCipherKeyPair;
                        if (keypair != null)
                        {
                            privateKey = keypair.Private;
                            break;
                        }

                        // read next object
                        pemObject = pemReader.ReadObject();
                    }
                }
                finally
                {
                    pemReader.Reader.Dispose();
                }
            }

            if (privateKey == null)
            {
                throw new ServiceResultException("PEM data blob does not contain a private key.");
            }

            using (var cfrg = new CertificateFactoryRandomGenerator())
            {
                SecureRandom random = new SecureRandom(cfrg);
                Org.BouncyCastle.X509.X509Certificate x509 = new X509CertificateParser().ReadCertificate(certificate.RawData);
                return CreateCertificateWithPrivateKey(x509, certificate.FriendlyName, privateKey, random);
            }
        }

        /// <summary>
        /// Returns a byte array containing the cert in PEM format.
        /// </summary>
        public static byte[] ExportCertificateAsPEM(X509Certificate2 certificate)
        {
            Org.BouncyCastle.X509.X509Certificate bcCert = new Org.BouncyCastle.X509.X509CertificateParser().ReadCertificate(certificate.RawData);

            using (var memoryStream = new MemoryStream())
            {
                using (var textWriter = new StreamWriter(memoryStream))
                {
                    var pemWriter = new PemWriter(textWriter);
                    pemWriter.WriteObject(bcCert);
                    pemWriter.Writer.Flush();
                    return memoryStream.ToArray();
                }
            }
        }

        /// <summary>
        /// Returns a byte array containing the private key in PEM format.
        /// </summary>
        public static byte[] ExportPrivateKeyAsPEM(
            X509Certificate2 certificate
            )
        {
            RsaPrivateCrtKeyParameters privateKeyParameter = GetPrivateKeyParameter(certificate);
            using (TextWriter textWriter = new StringWriter())
            {
                // write private key as PKCS#8
                PrivateKeyInfo privateKeyInfo = PrivateKeyInfoFactory.CreatePrivateKeyInfo(privateKeyParameter);
                byte[] serializedPrivateBytes = privateKeyInfo.ToAsn1Object().GetDerEncoded();
                string serializedPrivate = Convert.ToBase64String(serializedPrivateBytes);
                textWriter.WriteLine("-----BEGIN PRIVATE KEY-----");
                while (serializedPrivate.Length > 64)
                {
                    textWriter.WriteLine(serializedPrivate.Substring(0, 64));
                    serializedPrivate = serializedPrivate.Substring(64);
                }
                textWriter.WriteLine(serializedPrivate);
                textWriter.WriteLine("-----END PRIVATE KEY-----");
                return Encoding.ASCII.GetBytes(textWriter.ToString());
            }
        }

        /// <summary>
        /// Verify the signature of a self signed certificate.
        /// </summary>
        public static bool VerifySelfSigned(X509Certificate2 cert)
        {
            try
            {
                Org.BouncyCastle.X509.X509Certificate bcCert = new Org.BouncyCastle.X509.X509CertificateParser().ReadCertificate(cert.RawData);
                bcCert.Verify(bcCert.GetPublicKey());
            }
            catch
            {
                return false;
            }
            return true;
        }

        /// <summary>
        /// Return the key usage flags of a certificate.
        /// </summary>
        public static X509KeyUsageFlags GetKeyUsage(X509Certificate2 cert)
        {
            X509KeyUsageFlags allFlags = X509KeyUsageFlags.None;
            foreach (X509KeyUsageExtension ext in cert.Extensions.OfType<X509KeyUsageExtension>())
            {
                allFlags |= ext.KeyUsages;
            }
            return allFlags;
        }

        /// <summary>
        /// Verify RSA key pair of two certificates.
        /// </summary>
        public static bool VerifyRSAKeyPair(
            X509Certificate2 certWithPublicKey,
            X509Certificate2 certWithPrivateKey,
            bool throwOnError = false)
        {
            bool result = false;
            RSA rsaPrivateKey = null;
            RSA rsaPublicKey = null;
            try
            {
                // verify the public and private key match
                rsaPrivateKey = certWithPrivateKey.GetRSAPrivateKey();
#if NETSTANDARD2_1
                // on .NET Core 3 
                rsaPrivateKey.ExportParameters(true);
#endif
                rsaPublicKey = certWithPublicKey.GetRSAPublicKey();
                X509KeyUsageFlags keyUsage = GetKeyUsage(certWithPublicKey);
                if ((keyUsage & X509KeyUsageFlags.DataEncipherment) != 0)
                {
                    result = VerifyRSAKeyPairCrypt(rsaPublicKey, rsaPrivateKey);
                }
                else if ((keyUsage & X509KeyUsageFlags.DigitalSignature) != 0)
                {
                    result = VerifyRSAKeyPairSign(rsaPublicKey, rsaPrivateKey);
                }
                else
                {
                    throw new CryptographicException("Don't know how to verify the public/private key pair.");
                }
            }
            catch (Exception)
            {
                if (throwOnError)
                {
                    throwOnError = false;
                    throw;
                }
            }
            finally
            {
                RsaUtils.RSADispose(rsaPrivateKey);
                RsaUtils.RSADispose(rsaPublicKey);
                if (!result && throwOnError)
                {
                    throw new CryptographicException("The public/private key pair in the certficates do not match.");
                }
            }
            return result;
        }

        /// <summary>
        /// Returns the size of the public key and disposes RSA key.
        /// </summary>
        /// <param name="certificate">The certificate</param>
        public static int GetRSAPublicKeySize(X509Certificate2 certificate)
        {
            RSA rsaPublicKey = null;
            try
            {
                rsaPublicKey = certificate.GetRSAPublicKey();
                return rsaPublicKey.KeySize;
            }
            finally
            {
                RsaUtils.RSADispose(rsaPublicKey);
            }
        }
        #endregion

        #region Private Methods
        /// <summary>
        /// Sets the parameters to suitable defaults.
        /// </summary>
        private static X500DistinguishedName SetSuitableDefaults(
            ref string applicationUri,
            ref string applicationName,
            ref string subjectName,
            ref IList<String> domainNames,
            ref ushort keySize,
            ref ushort lifetimeInMonths)
        {
            // enforce recommended keysize unless lower value is enforced.
            if (keySize < 1024)
            {
                keySize = DefaultKeySize;
            }

            if (keySize % 1024 != 0)
            {
                throw new ArgumentNullException(nameof(keySize), "KeySize must be a multiple of 1024.");
            }

            // enforce minimum lifetime.
            if (lifetimeInMonths < 1)
            {
                lifetimeInMonths = 1;
            }

            // parse the subject name if specified.
            List<string> subjectNameEntries = null;

            if (!String.IsNullOrEmpty(subjectName))
            {
                subjectNameEntries = Utils.ParseDistinguishedName(subjectName);
            }

            // check the application name.
            if (String.IsNullOrEmpty(applicationName))
            {
                if (subjectNameEntries == null)
                {
                    throw new ArgumentNullException(nameof(applicationName), "Must specify a applicationName or a subjectName.");
                }

                // use the common name as the application name.
                for (int ii = 0; ii < subjectNameEntries.Count; ii++)
                {
                    if (subjectNameEntries[ii].StartsWith("CN="))
                    {
                        applicationName = subjectNameEntries[ii].Substring(3).Trim();
                        break;
                    }
                }
            }

            if (String.IsNullOrEmpty(applicationName))
            {
                throw new ArgumentNullException(nameof(applicationName), "Must specify a applicationName or a subjectName.");
            }

            // remove special characters from name.
            StringBuilder buffer = new StringBuilder();

            for (int ii = 0; ii < applicationName.Length; ii++)
            {
                char ch = applicationName[ii];

                if (Char.IsControl(ch) || ch == '/' || ch == ',' || ch == ';')
                {
                    ch = '+';
                }

                buffer.Append(ch);
            }

            applicationName = buffer.ToString();

            // ensure at least one host name.
            if (domainNames == null || domainNames.Count == 0)
            {
                domainNames = new List<string>();
                domainNames.Add(Utils.GetHostName());
            }

            // create the application uri.
            if (String.IsNullOrEmpty(applicationUri))
            {
                StringBuilder builder = new StringBuilder();

                builder.Append("urn:");
                builder.Append(domainNames[0]);
                builder.Append(":");
                builder.Append(applicationName);

                applicationUri = builder.ToString();
            }

            Uri uri = Utils.ParseUri(applicationUri);

            if (uri == null)
            {
                throw new ArgumentNullException(nameof(applicationUri), "Must specify a valid URL.");
            }

            // create the subject name,
            if (String.IsNullOrEmpty(subjectName))
            {
                subjectName = Utils.Format("CN={0}", applicationName);
            }

            if (!subjectName.Contains("CN="))
            {
                subjectName = Utils.Format("CN={0}", subjectName);
            }

            if (domainNames != null && domainNames.Count > 0)
            {
                if (!subjectName.Contains("DC=") && !subjectName.Contains("="))
                {
                    subjectName += Utils.Format(", DC={0}", domainNames[0]);
                }
                else
                {
                    subjectName = Utils.ReplaceDCLocalhost(subjectName, domainNames[0]);
                }
            }

            return new X500DistinguishedName(subjectName);
        }

        private static HashAlgorithmName GetRSAHashAlgorithmName(uint hashSizeInBits)
        {
            if (hashSizeInBits <= 160)
            {
                return HashAlgorithmName.SHA1;
            }
            else if (hashSizeInBits <= 256)
            {
                return HashAlgorithmName.SHA256;
            }
            else if (hashSizeInBits <= 384)
            {
                return HashAlgorithmName.SHA384;
            }
            else
            {
                return HashAlgorithmName.SHA512;
            }
        }

        /// <summary>
        /// Build the Authority Key Identifier from an Issuer CA certificate.
        /// </summary>
        /// <param name="issuerCaCertificate">The issuer CA certificate</param>
        private static System.Security.Cryptography.X509Certificates.X509Extension BuildAuthorityKeyIdentifier(X509Certificate2 issuerCaCertificate)
        {
            // force exception if SKI is not present
            var ski = issuerCaCertificate.Extensions.OfType<X509SubjectKeyIdentifierExtension>().Single();
            return BuildAuthorityKeyIdentifier(issuerCaCertificate.SubjectName, issuerCaCertificate.GetSerialNumber(), ski);
        }

        /// <summary>
        /// Build the Subject Alternative name extension (for OPC UA application certs)
        /// </summary>
        /// <param name="applicationUri">The application Uri</param>
        /// <param name="domainNames">The domain names. DNS Hostnames, IPv4 or IPv6 addresses</param>
        private static System.Security.Cryptography.X509Certificates.X509Extension BuildSubjectAlternativeName(string applicationUri, IList<string> domainNames)
        {
            var sanBuilder = new SubjectAlternativeNameBuilder();
            sanBuilder.AddUri(new Uri(applicationUri));
            foreach (string domainName in domainNames)
            {
                IPAddress ipAddr;
                if (String.IsNullOrWhiteSpace(domainName))
                {
                    continue;
                }
                if (IPAddress.TryParse(domainName, out ipAddr))
                {
                    sanBuilder.AddIpAddress(ipAddr);
                }
                else
                {
                    sanBuilder.AddDnsName(domainName);
                }
            }

            return sanBuilder.Build();
        }

        /// <summary>
        /// Build the CRL number.
        /// </summary>
        private static System.Security.Cryptography.X509Certificates.X509Extension BuildCRLNumber(System.Numerics.BigInteger crlNumber)
        {
            AsnWriter writer = new AsnWriter(AsnEncodingRules.DER);
            writer.WriteInteger(crlNumber);
            return new System.Security.Cryptography.X509Certificates.X509Extension("2.5.29.20", writer.Encode(), false);
        }


        /// <summary>
        /// Build the X509 Authority Key extension.
        /// </summary>
        /// <param name="issuerName">The distinguished name of the issuer</param>
        /// <param name="issuerSerialNumber">The serial number of the issuer</param>
        /// <param name="ski">The subject key identifier extension to use</param>
        private static System.Security.Cryptography.X509Certificates.X509Extension BuildAuthorityKeyIdentifier(
            X500DistinguishedName issuerName,
            byte[] issuerSerialNumber,
            X509SubjectKeyIdentifierExtension ski
            )
        {
            {
                AsnWriter writer = new AsnWriter(AsnEncodingRules.DER);
                writer.PushSequence();

                if (ski != null)
                {
                    Asn1Tag keyIdTag = new Asn1Tag(TagClass.ContextSpecific, 0);
                    writer.WriteOctetString(HexToByteArray(ski.SubjectKeyIdentifier), keyIdTag);
                }

                Asn1Tag issuerNameTag = new Asn1Tag(TagClass.ContextSpecific, 1);
                writer.PushSequence(issuerNameTag);

                // Add the tag to constructed context-specific 4 (GeneralName.directoryName)
                Asn1Tag directoryNameTag = new Asn1Tag(TagClass.ContextSpecific, 4, true);
                writer.PushSetOf(directoryNameTag);
                byte[] issuerNameRaw = issuerName.RawData;
                writer.WriteEncodedValue(issuerNameRaw);
                writer.PopSetOf(directoryNameTag);
                writer.PopSequence(issuerNameTag);

                Asn1Tag issuerSerialTag = new Asn1Tag(TagClass.ContextSpecific, 2);
                System.Numerics.BigInteger issuerSerial = new System.Numerics.BigInteger(issuerSerialNumber);
                writer.WriteInteger(issuerSerial, issuerSerialTag);

                writer.PopSequence();
                return new System.Security.Cryptography.X509Certificates.X509Extension("2.5.29.35", writer.Encode(), false);
            }
        }

        /// <summary>
        /// Build the Authority information Access extension.
        /// </summary>
        /// <param name="caIssuerUrls">Array of CA Issuer Urls</param>
        /// <param name="ocspResponder">optional, the OCSP responder </param>
        private static System.Security.Cryptography.X509Certificates.X509Extension BuildX509AuthorityInformationAccess(
            string[] caIssuerUrls,
            string ocspResponder = null
            )
        {
            if (String.IsNullOrEmpty(ocspResponder) &&
               (caIssuerUrls == null || caIssuerUrls.Length == 0))
            {
                throw new ArgumentNullException(nameof(caIssuerUrls), "One CA Issuer Url or OCSP responder is required for the extension.");
            }

            var context0 = new Asn1Tag(TagClass.ContextSpecific, 0, true);
            Asn1Tag generalNameUriChoice = new Asn1Tag(TagClass.ContextSpecific, 6);
            {
                AsnWriter writer = new AsnWriter(AsnEncodingRules.DER);
                writer.PushSequence();
                if (caIssuerUrls != null)
                {
                    foreach (var caIssuerUrl in caIssuerUrls)
                    {
                        writer.PushSequence();
                        writer.WriteObjectIdentifier("1.3.6.1.5.5.7.48.2");
                        writer.WriteCharacterString(
                            UniversalTagNumber.IA5String,
                            caIssuerUrl,
                            generalNameUriChoice
                            );
                        writer.PopSequence();
                    }
                }
                if (!String.IsNullOrEmpty(ocspResponder))
                {
                    writer.PushSequence();
                    writer.WriteObjectIdentifier("1.3.6.1.5.5.7.48.1");
                    writer.WriteCharacterString(
                        UniversalTagNumber.IA5String,
                        ocspResponder,
                        generalNameUriChoice
                        );
                    writer.PopSequence();
                }
                writer.PopSequence();
                return new System.Security.Cryptography.X509Certificates.X509Extension("1.3.6.1.5.5.7.1.1", writer.Encode(), false);
            }
        }

        /// <summary>
        /// Build the CRL Distribution Point extension.
        /// </summary>
        /// <param name="distributionPoint">The CRL distribution point</param>
        private static System.Security.Cryptography.X509Certificates.X509Extension BuildX509CRLDistributionPoints(
            string distributionPoint
            )
        {
            var context0 = new Asn1Tag(TagClass.ContextSpecific, 0, true);
            Asn1Tag distributionPointChoice = context0;
            Asn1Tag fullNameChoice = context0;
            Asn1Tag generalNameUriChoice = new Asn1Tag(TagClass.ContextSpecific, 6);

            {
                AsnWriter writer = new AsnWriter(AsnEncodingRules.DER);
                writer.PushSequence();
                writer.PushSequence();
                writer.PushSequence(distributionPointChoice);
                writer.PushSequence(fullNameChoice);
                writer.WriteCharacterString(
                    UniversalTagNumber.IA5String,
                    distributionPoint,
                    generalNameUriChoice
                    );
                writer.PopSequence(fullNameChoice);
                writer.PopSequence(distributionPointChoice);
                writer.PopSequence();
                writer.PopSequence();
                return new System.Security.Cryptography.X509Certificates.X509Extension("2.5.29.31", writer.Encode(), false);
            }
        }

        /// <summary>
        /// Convert a hex string to a byte array.
        /// </summary>
        /// <param name="hexString">The hex string</param>
        internal static byte[] HexToByteArray(string hexString)
        {
            byte[] bytes = new byte[hexString.Length / 2];

            for (int i = 0; i < hexString.Length; i += 2)
            {
                string s = hexString.Substring(i, 2);
                bytes[i / 2] = byte.Parse(s, System.Globalization.NumberStyles.HexNumber, null);
            }

            return bytes;
        }

        /// <summary>
        /// helper to build alternate name domains list for certs.
        /// </summary>
        private static List<GeneralName> CreateSubjectAlternateNameDomains(IList<String> domainNames)
        {
            // subject alternate name
            List<GeneralName> generalNames = new List<GeneralName>();
            for (int i = 0; i < domainNames.Count; i++)
            {
                int domainType = GeneralName.OtherName;
                switch (Uri.CheckHostName(domainNames[i]))
                {
                    case UriHostNameType.Dns: domainType = GeneralName.DnsName; break;
                    case UriHostNameType.IPv4:
                    case UriHostNameType.IPv6: domainType = GeneralName.IPAddress; break;
                    default: continue;
                }
                generalNames.Add(new GeneralName(domainType, domainNames[i]));
            }
            return generalNames;
        }

        /// <summary>
        /// helper to get the Bouncy Castle hash algorithm name by hash size in bits.
        /// </summary>
        /// <param name="hashSizeInBits"></param>
        private static string GetRSAHashAlgorithm(uint hashSizeInBits)
        {
            if (hashSizeInBits <= 160)
            {
                return "SHA1WITHRSA";
            }

            if (hashSizeInBits <= 224)
            {
                return "SHA224WITHRSA";
            }
            else if (hashSizeInBits <= 256)
            {
                return "SHA256WITHRSA";
            }
            else if (hashSizeInBits <= 384)
            {
                return "SHA384WITHRSA";
            }
            else
            {
                return "SHA512WITHRSA";
            }
        }

        /// <summary>
        /// Get public key parameters from a X509Certificate2
        /// </summary>
        private static RsaKeyParameters GetPublicKeyParameter(X509Certificate2 certificate)
        {
            RSA rsa = null;
            try
            {
                rsa = certificate.GetRSAPublicKey();
                RSAParameters rsaParams = rsa.ExportParameters(false);
                return new RsaKeyParameters(
                    false,
                    new BigInteger(1, rsaParams.Modulus),
                    new BigInteger(1, rsaParams.Exponent));
            }
            finally
            {
                RsaUtils.RSADispose(rsa);
            }
        }

        /// <summary>
        /// Get private key parameters from a X509Certificate2.
        /// The private key must be exportable.
        /// </summary>
        private static RsaPrivateCrtKeyParameters GetPrivateKeyParameter(X509Certificate2 certificate)
        {
            RSA rsa = null;
            try
            {
                // try to get signing/private key from certificate passed in
                rsa = certificate.GetRSAPrivateKey();
                RSAParameters rsaParams = rsa.ExportParameters(true);
                RsaPrivateCrtKeyParameters keyParams = new RsaPrivateCrtKeyParameters(
                    new BigInteger(1, rsaParams.Modulus),
                    new BigInteger(1, rsaParams.Exponent),
                    new BigInteger(1, rsaParams.D),
                    new BigInteger(1, rsaParams.P),
                    new BigInteger(1, rsaParams.Q),
                    new BigInteger(1, rsaParams.DP),
                    new BigInteger(1, rsaParams.DQ),
                    new BigInteger(1, rsaParams.InverseQ));
                return keyParams;
            }
            finally
            {
                RsaUtils.RSADispose(rsa);
            }
        }

        /// <summary>
        /// Get the serial number from a certificate as BigInteger.
        /// </summary>
        private static BigInteger GetSerialNumber(X509Certificate2 certificate)
        {
            byte[] serialNumber = certificate.GetSerialNumber();
            Array.Reverse(serialNumber);
            return new BigInteger(1, serialNumber);
        }

        /// <summary>
        /// Read the Crl number from a X509Crl.
        /// </summary>
        public static BigInteger GetCrlNumber(X509Crl crl)
        {
            BigInteger crlNumber = BigInteger.One;
            try
            {
                Asn1Object asn1Object = GetExtensionValue(crl, X509Extensions.CrlNumber);
                if (asn1Object != null)
                {
                    crlNumber = CrlNumber.GetInstance(asn1Object).PositiveValue;
                }
            }
            finally
            {
            }
            return crlNumber;
        }

        /// <summary>
        /// Get the value of an extension oid.
        /// </summary>
        private static Asn1Object GetExtensionValue(IX509Extension extension, DerObjectIdentifier oid)
        {
            Asn1OctetString asn1Octet = extension.GetExtensionValue(oid);
            if (asn1Octet != null)
            {
                return X509ExtensionUtilities.FromExtensionValue(asn1Octet);
            }
            return null;
        }

        /// <summary>
        /// Patch serial number in a Url. byte version.
        /// </summary>
        private static string PatchExtensionUrl(string extensionUrl, byte[] serialNumber)
        {
            string serial = BitConverter.ToString(serialNumber).Replace("-", "");
            return PatchExtensionUrl(extensionUrl, serial);
        }

        /// <summary>
        /// Patch serial number in a Url. string version.
        /// </summary>
        private static string PatchExtensionUrl(string extensionUrl, string serial)
        {
            return extensionUrl.Replace("%serial%", serial.ToLower());
        }

        /// <summary>
        /// Determines whether the certificate is issued by a Certificate Authority.
        /// </summary>
        public static bool IsCertificateAuthority(X509Certificate2 certificate)
        {
            X509BasicConstraintsExtension constraints = null;

            for (int ii = 0; ii < certificate.Extensions.Count; ii++)
            {
                constraints = certificate.Extensions[ii] as X509BasicConstraintsExtension;

                if (constraints != null)
                {
                    return constraints.CertificateAuthority;
                }
            }

            return false;
        }

        /// <summary>
        /// Get the certificate by issuer and serial number.
        /// </summary>
        private static async Task<X509Certificate2> FindIssuerCABySerialNumberAsync(
            ICertificateStore store,
            string issuer,
            string serialnumber)
        {
            X509Certificate2Collection certificates = await store.Enumerate();

            foreach (var certificate in certificates)
            {
                if (Utils.CompareDistinguishedName(certificate.Subject, issuer) &&
                    Utils.IsEqual(certificate.SerialNumber, serialnumber))
                {
                    return certificate;
                }
            }

            return null;
        }

        /// <summary>
        /// Return the authority key identifier in the certificate.
        /// </summary>
        private static X509AuthorityKeyIdentifierExtension FindAuthorityKeyIdentifier(X509Certificate2 certificate)
        {
            for (int ii = 0; ii < certificate.Extensions.Count; ii++)
            {
                System.Security.Cryptography.X509Certificates.X509Extension extension = certificate.Extensions[ii];

                switch (extension.Oid.Value)
                {
                    case X509AuthorityKeyIdentifierExtension.AuthorityKeyIdentifierOid:
                    case X509AuthorityKeyIdentifierExtension.AuthorityKeyIdentifier2Oid:
                    {
                        return new X509AuthorityKeyIdentifierExtension(extension, extension.Critical);
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Create a X509Certificate2 with a private key by combining 
        /// a bouncy castle X509Certificate and a private key
        /// </summary>
        private static X509Certificate2 CreateCertificateWithPrivateKey(
            Org.BouncyCastle.X509.X509Certificate certificate,
            string friendlyName,
            AsymmetricKeyParameter privateKey,
            SecureRandom random)
        {
            // create pkcs12 store for cert and private key
            using (MemoryStream pfxData = new MemoryStream())
            {
                Pkcs12StoreBuilder builder = new Pkcs12StoreBuilder();
                builder.SetUseDerEncoding(true);
                Pkcs12Store pkcsStore = builder.Build();
                X509CertificateEntry[] chain = new X509CertificateEntry[1];
                string passcode = Guid.NewGuid().ToString();
                chain[0] = new X509CertificateEntry(certificate);
                if (string.IsNullOrEmpty(friendlyName))
                {
                    friendlyName = GetCertificateCommonName(certificate);
                }
                pkcsStore.SetKeyEntry(friendlyName, new AsymmetricKeyEntry(privateKey), chain);
                pkcsStore.Save(pfxData, passcode.ToCharArray(), random);

                // merge into X509Certificate2
                return CreateCertificateFromPKCS12(pfxData.ToArray(), passcode);
            }
        }

        /// <summary>
        /// Read the Common Name from a certificate.
        /// </summary>
        private static string GetCertificateCommonName(Org.BouncyCastle.X509.X509Certificate certificate)
        {
            var subjectDN = certificate.SubjectDN.GetValueList(X509Name.CN);
            if (subjectDN.Count > 0)
            {
                return subjectDN[0].ToString();
            }
            return string.Empty;
        }


        private static bool VerifyRSAKeyPairCrypt(
            RSA rsaPublicKey,
            RSA rsaPrivateKey)
        {
            Opc.Ua.Test.RandomSource randomSource = new Opc.Ua.Test.RandomSource();
            int blockSize = RsaUtils.GetPlainTextBlockSize(rsaPrivateKey, RsaUtils.Padding.OaepSHA1);
            byte[] testBlock = new byte[blockSize];
            randomSource.NextBytes(testBlock, 0, blockSize);
            byte[] encryptedBlock = rsaPublicKey.Encrypt(testBlock, RSAEncryptionPadding.OaepSHA1);
            byte[] decryptedBlock = rsaPrivateKey.Decrypt(encryptedBlock, RSAEncryptionPadding.OaepSHA1);
            if (decryptedBlock != null)
            {
                return Utils.IsEqual(testBlock, decryptedBlock);
            }
            return false;
        }

        private static bool VerifyRSAKeyPairSign(
            RSA rsaPublicKey,
            RSA rsaPrivateKey)
        {
            Opc.Ua.Test.RandomSource randomSource = new Opc.Ua.Test.RandomSource();
            int blockSize = RsaUtils.GetPlainTextBlockSize(rsaPrivateKey, RsaUtils.Padding.OaepSHA1);
            byte[] testBlock = new byte[blockSize];
            randomSource.NextBytes(testBlock, 0, blockSize);
            byte[] signature = rsaPrivateKey.SignData(testBlock, HashAlgorithmName.SHA1, RSASignaturePadding.Pkcs1);
            return rsaPublicKey.VerifyData(testBlock, signature, HashAlgorithmName.SHA1, RSASignaturePadding.Pkcs1);
        }


        private class Password
            : IPasswordFinder
        {
            private readonly char[] password;

            public Password(
                char[] word)
            {
                this.password = (char[])word.Clone();
            }

            public char[] GetPassword()
            {
                return (char[])password.Clone();
            }
        }

        #endregion

        private static Dictionary<string, X509Certificate2> m_certificates = new Dictionary<string, X509Certificate2>();
        //private static List<X509Certificate2> m_temporaryKeyContainers = new List<X509Certificate2>();
    }
}
#endif