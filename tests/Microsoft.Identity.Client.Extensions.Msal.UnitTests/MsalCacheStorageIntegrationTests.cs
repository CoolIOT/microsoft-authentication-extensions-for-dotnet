﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Microsoft.Identity.Client.Extensions.Msal.UnitTests
{
    /// <summary>
    /// These tests write data to disk / key chain / key ring etc. 
    /// </summary>
    [TestClass]
    public class MsalCacheStorageIntegrationTests
    {
        public static readonly string CacheFilePath = Path.Combine(Path.GetTempPath(), Path.GetTempFileName());
        private readonly TraceSource _logger = new TraceSource("TestSource", SourceLevels.All);
        private static StorageCreationProperties s_storageCreationProperties;

        public TestContext TestContext { get; set; }

        [ClassInitialize]
        public static void ClassInitialize(TestContext _)
        {
            var builder = new StorageCreationPropertiesBuilder(
                Path.GetFileName(CacheFilePath),
                Path.GetDirectoryName(CacheFilePath));
            builder = builder.WithMacKeyChain(serviceName: "Microsoft.Developer.IdentityService", accountName: "MSALCache");

            // Tests run on machines without Libsecret
            builder = builder.WithLinuxUnprotectedFile();
            s_storageCreationProperties = builder.Build();
        }

        [TestInitialize]
        public void TestiInitialize()
        {
            CleanTestData();
        }

        [TestCleanup]
        public void TestCleanup()
        {
            CleanTestData();
        }

        [TestMethod]
        public void MsalTestUserDirectory()
        {
            Assert.AreEqual(MsalCacheHelper.UserRootDirectory,
                Environment.OSVersion.Platform == PlatformID.Win32NT
                    ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
                    : Environment.GetEnvironmentVariable("HOME"));
        }

        [RunOnOSX]
        public void CacheStorageFactoryMac()
        {
            Storage store = Storage.Create(s_storageCreationProperties, logger: _logger);
            Assert.IsTrue(store.CacheAccessor is MacKeychainAccessor);
            store.VerifyPersistence();

            store = Storage.Create(s_storageCreationProperties, logger: _logger);
            Assert.IsTrue(store.CacheAccessor is MacKeychainAccessor);
        }

        [RunOnWindows]
        public void CacheStorageFactoryWindows()
        {
            Storage store = Storage.Create(s_storageCreationProperties, logger: _logger);
            Assert.IsTrue(store.CacheAccessor is DpApiEncryptedFileAccessor);
            store.VerifyPersistence();

            store = Storage.Create(s_storageCreationProperties, logger: _logger);
            Assert.IsTrue(store.CacheAccessor is DpApiEncryptedFileAccessor);
        }

        [TestMethod]
        public void CacheFallback()
        {
            const string data = "data";
            var plaintextStorage = new StorageCreationPropertiesBuilder(
                    Path.GetFileName(CacheFilePath + "fallback"),
                    Path.GetDirectoryName(CacheFilePath))
                .WithUnprotectedFile()
                .Build();

            Storage unprotectedStore = Storage.Create(plaintextStorage, _logger);
            Assert.IsTrue(unprotectedStore.CacheAccessor is FileAccessor);

            unprotectedStore.VerifyPersistence();
            unprotectedStore.WriteData(Encoding.UTF8.GetBytes(data));

            // Unproteced cache file should exist
            Assert.IsTrue(File.Exists(plaintextStorage.CacheFilePath));

            string dataReadFromPlaintext = File.ReadAllText(plaintextStorage.CacheFilePath);

            Assert.AreEqual(data, dataReadFromPlaintext);
        }

        [RunOnLinux]
        public void CacheStorageFactory_WithFallback_Linux()
        {
            var storageWithKeyRing = new StorageCreationPropertiesBuilder(
                    Path.GetFileName(CacheFilePath),
                    Path.GetDirectoryName(CacheFilePath))
                .WithMacKeyChain(serviceName: "Microsoft.Developer.IdentityService", accountName: "MSALCache")
                .WithLinuxKeyring(
                    schemaName: "msal.cache",
                    collection: "default",
                    secretLabel: "MSALCache",
                    attribute1: new KeyValuePair<string, string>("MsalClientID", "Microsoft.Developer.IdentityService"),
                    attribute2: new KeyValuePair<string, string>("MsalClientVersion", "1.0.0.0"))
                .Build();

            // Tests run on machines without Libsecret
            Storage store = Storage.Create(storageWithKeyRing, logger: _logger);
            Assert.IsTrue(store.CacheAccessor is LinuxKeyringAccessor);

            // ADO Linux test agents do not have libsecret installed by default
            // If you run this test on a Linux box with UI / LibSecret, then this test will fail
            // because the statement below will not throw.
            AssertException.Throws<MsalCachePersistenceException>(
                () => store.VerifyPersistence());

            Storage unprotectedStore = Storage.Create(s_storageCreationProperties, _logger);
            Assert.IsTrue(unprotectedStore.CacheAccessor is FileAccessor);

            unprotectedStore.VerifyPersistence();

            unprotectedStore.WriteData(new byte[] { 2, 3 });

            // Unproteced cache file should exist
            Assert.IsTrue(File.Exists(s_storageCreationProperties.CacheFilePath));

            // Mimic another sdk client to check libsecret availability by calling
            // MsalCacheStorage.VerifyPeristence() -> LinuxKeyringAccessor.CreateForPersistenceValidation()
            AssertException.Throws<MsalCachePersistenceException>(
                () => store.VerifyPersistence());

            // Verify above call doesn't delete existing cache file
            Assert.IsTrue(File.Exists(s_storageCreationProperties.CacheFilePath));
        }

        [TestMethod]
        public void MsalNewStoreNoFile()
        {
            var store = Storage.Create(s_storageCreationProperties, logger: _logger);
            Assert.IsFalse(store.ReadData().Any());
        }

        [TestMethod]
        public void MsalWriteEmptyData()
        {
            var store = Storage.Create(s_storageCreationProperties, logger: _logger);
            Assert.ThrowsException<ArgumentNullException>(() => store.WriteData(null));

            store.WriteData(new byte[0]);

            Assert.IsFalse(store.ReadData().Any());
        }

        [TestMethod]
        public void MsalWriteGoodData()
        {
            var store = Storage.Create(s_storageCreationProperties, logger: _logger);
            Assert.ThrowsException<ArgumentNullException>(() => store.WriteData(null));

            byte[] data = { 2, 2, 3 };
            byte[] data2 = { 2, 2, 3, 4, 4 };
            store.WriteData(data);
            Assert.IsTrue(Enumerable.SequenceEqual(store.ReadData(), data));

            store.WriteData(data);
            store.WriteData(data2);
            store.WriteData(data);
            store.WriteData(data2);
            Assert.IsTrue(Enumerable.SequenceEqual(store.ReadData(), data2));
        }

        [TestMethod]
        public void MsalTestClear()
        {
            var store = Storage.Create(s_storageCreationProperties, logger: _logger);
            store.ReadData();

            var store2 = Storage.Create(s_storageCreationProperties, logger: _logger);
            AssertException.Throws<ArgumentNullException>(() => store.WriteData(null));

            byte[] data = { 2, 2, 3 };
            store.WriteData(data);
            store2.ReadData();

            Assert.IsTrue(Enumerable.SequenceEqual(store.ReadData(), data));
            Assert.IsTrue(File.Exists(CacheFilePath));

            store.Clear();

            Assert.IsFalse(store.ReadData().Any());
            Assert.IsFalse(store2.ReadData().Any());
            Assert.IsFalse(File.Exists(CacheFilePath));
        }

        private void CleanTestData()
        {
            var store = Storage.Create(s_storageCreationProperties, logger: _logger);
            store.Clear();
        }
    }
}
