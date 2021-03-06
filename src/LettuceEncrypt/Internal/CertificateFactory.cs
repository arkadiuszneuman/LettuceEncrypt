﻿// Copyright (c) Nate McMaster.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Certes;
using Certes.Acme;
using Certes.Acme.Resource;
using LettuceEncrypt.Accounts;
using LettuceEncrypt.Acme;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

#if NETSTANDARD2_0
using IHostApplicationLifetime = Microsoft.Extensions.Hosting.IApplicationLifetime;
#endif

namespace LettuceEncrypt.Internal
{
    internal class CertificateFactory
    {
        private readonly TermsOfServiceChecker _tosChecker;
        private readonly IOptions<LettuceEncryptOptions> _options;
        private readonly IHttpChallengeResponseStore _challengeStore;
        private readonly IAccountStore _accountRepository;
        private readonly ICertificateAuthorityConfiguration _certificateAuthority;
        private readonly ILogger _logger;
        private readonly TlsAlpnChallengeResponder _tlsAlpnChallengeResponder;
        private readonly TaskCompletionSource<object?> _appStarted;
        private AcmeContext? _context;
        private IAccountContext? _accountContext;

        public CertificateFactory(
            TermsOfServiceChecker tosChecker,
            IOptions<LettuceEncryptOptions> options,
            IHttpChallengeResponseStore challengeStore,
            IAccountStore? accountRepository,
            ILogger logger,
            IHostApplicationLifetime appLifetime,
            TlsAlpnChallengeResponder tlsAlpnChallengeResponder,
            ICertificateAuthorityConfiguration certificateAuthority)
        {
            _tosChecker = tosChecker;
            _options = options;
            _challengeStore = challengeStore;
            _logger = logger;
            _tlsAlpnChallengeResponder = tlsAlpnChallengeResponder;
            _certificateAuthority = certificateAuthority;

            _appStarted = new TaskCompletionSource<object?>();
            appLifetime.ApplicationStarted.Register(() => _appStarted.TrySetResult(null));
            if (appLifetime.ApplicationStarted.IsCancellationRequested)
            {
                _appStarted.TrySetResult(null);
            }

            _accountRepository = accountRepository ?? new FileSystemAccountStore(logger, certificateAuthority);
        }

        public async Task<AccountModel> GetOrCreateAccountAsync(CancellationToken cancellationToken)
        {
            var account = await _accountRepository.GetAccountAsync(cancellationToken);

            var acmeAccountKey = account != null
                ? KeyFactory.FromDer(account.PrivateKey)
                : null;

            var directoryUri = _certificateAuthority.AcmeDirectoryUri;
            _logger.LogInformation("Using certificate authority {directoryUri}", directoryUri);
            _context = new AcmeContext(directoryUri, acmeAccountKey);

            if (account != null && await ExistingAccountIsValidAsync(_context))
            {
                return account;
            }

            return await CreateAccount(cancellationToken);
        }

        private async Task<AccountModel> CreateAccount(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Debug.Assert(_context != null);

            var tosUri = await _context.TermsOfService();

            _tosChecker.EnsureTermsAreAccepted(tosUri);

            var options = _options.Value;
            _logger.LogInformation("Creating new account for {email}", options.EmailAddress);
            _accountContext = await _context.NewAccount(options.EmailAddress, termsOfServiceAgreed: true);
            _logger.LogAcmeAction("NewRegistration");

            if (!int.TryParse(_accountContext.Location.Segments.Last(), out var accountId))
            {
                accountId = 0;
            }

            var account = new AccountModel
            {
                Id = accountId,
                EmailAddresses = new[] {options.EmailAddress},
                PrivateKey = _context!.AccountKey.ToDer(),
            };

            await _accountRepository.SaveAccountAsync(account, cancellationToken);

            return account;
        }

        private async Task<bool> ExistingAccountIsValidAsync(AcmeContext context)
        {
            // double checks the account is still valid
            Account existingAccount;
            try
            {
                _accountContext = await context.Account();
                existingAccount = await _accountContext.Resource();
            }
            catch (AcmeRequestException exception)
            {
                _logger.LogWarning(
                    "An account key was found, but could not be matched to a valid account. Validation error: {acmeError}",
                    exception.Error);
                return false;
            }

            if (existingAccount.Status != AccountStatus.Valid)
            {
                _logger.LogWarning(
                    "An account key was found, but the account is no longer valid. Account status: {status}." +
                    "A new account will be registered.",
                    existingAccount.Status);
                return false;
            }

            _logger.LogInformation("Using existing account for {contact}", existingAccount.Contact);

            if (existingAccount.TermsOfServiceAgreed != true)
            {
                var tosUri = await _context.TermsOfService();
                _tosChecker.EnsureTermsAreAccepted(tosUri);
                await _accountContext.Update(agreeTermsOfService: true);
            }

            return true;
        }

        public async Task<X509Certificate2> CreateCertificateAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Debug.Assert(_context != null);
            Debug.Assert(_accountContext != null);

            IOrderContext? orderContext = null;
            var orderListContext = await _accountContext!.Orders();
            if (orderListContext != null)
            {
                var expectedDomains = new HashSet<string>(_options.Value.DomainNames);
                var orders = await orderListContext.Orders();
                foreach (var order in orders)
                {
                    var orderDetails = await order.Resource();
                    if (orderDetails.Status != OrderStatus.Pending)
                    {
                        continue;
                    }

                    var orderDomains = orderDetails
                        .Identifiers
                        .Where(i => i.Type == IdentifierType.Dns)
                        .Select(s => s.Value);

                    if (expectedDomains.SetEquals(orderDomains))
                    {
                        _logger.LogDebug("Found an existing order for a certificate");
                        orderContext = order;
                        break;
                    }
                }
            }

            if (orderContext == null)
            {
                _logger.LogDebug("Creating new order for a certificate");
                orderContext = await _context!.NewOrder(_options.Value.DomainNames);
            }

            cancellationToken.ThrowIfCancellationRequested();
            var authorizations = await orderContext.Authorizations();

            cancellationToken.ThrowIfCancellationRequested();
            await Task.WhenAll(BeginValidateAllAuthorizations(authorizations, cancellationToken));

            cancellationToken.ThrowIfCancellationRequested();
            return await CompleteCertificateRequestAsync(orderContext, cancellationToken);
        }

        private IEnumerable<Task> BeginValidateAllAuthorizations(IEnumerable<IAuthorizationContext> authorizations,
            CancellationToken cancellationToken)
        {
            foreach (var authorization in authorizations)
            {
                yield return ValidateDomainOwnershipAsync(authorization, cancellationToken);
            }
        }

        private async Task ValidateDomainOwnershipAsync(IAuthorizationContext authorizationContext,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var authorization = await authorizationContext.Resource();
            var domainName = authorization.Identifier.Value;

            if (authorization.Status == AuthorizationStatus.Valid)
            {
                // Short circuit if authorization is already complete
                return;
            }

            _logger.LogDebug("Requesting authorization to create certificate for {domainName}", domainName);

            cancellationToken.ThrowIfCancellationRequested();

            if (_tlsAlpnChallengeResponder.IsEnabled)
            {
                await PrepareTlsAlpnChallengeResponseAsync(authorizationContext, domainName, cancellationToken);
            }

            await PrepareHttpChallengeResponseAsync(authorizationContext, domainName, cancellationToken);

            var retries = 60;
            var delay = TimeSpan.FromSeconds(2);

            try
            {
                while (retries > 0)
                {
                    retries--;

                    cancellationToken.ThrowIfCancellationRequested();

                    authorization = await authorizationContext.Resource();

                    _logger.LogAcmeAction("GetAuthorization");

                    switch (authorization.Status)
                    {
                        case AuthorizationStatus.Valid:
                            return;
                        case AuthorizationStatus.Pending:
                            await Task.Delay(delay, cancellationToken);
                            continue;
                        case AuthorizationStatus.Invalid:
                            throw InvalidAuthorizationError(authorization);
                        case AuthorizationStatus.Revoked:
                            throw new InvalidOperationException(
                                $"The authorization to verify domainName '{domainName}' has been revoked.");
                        case AuthorizationStatus.Expired:
                            throw new InvalidOperationException(
                                $"The authorization to verify domainName '{domainName}' has expired.");
                        default:
                            throw new ArgumentOutOfRangeException("authorization",
                                "Unexpected response from server while validating domain ownership.");
                    }
                }

                throw new TimeoutException("Timed out waiting for domain ownership validation.");
            }
            finally
            {
                if (_tlsAlpnChallengeResponder.IsEnabled)
                {
                    // cleanup after authorization is done to skip unnecessary cert lookup on all incoming SSL connections
                    _tlsAlpnChallengeResponder.DiscardChallenge(domainName);
                }
            }
        }

        private async Task PrepareHttpChallengeResponseAsync(
            IAuthorizationContext authorizationContext,
            string domainName,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var httpChallenge = await authorizationContext.Http();
            if (httpChallenge == null)
            {
                throw new InvalidOperationException(
                    $"Did not receive challenge information for challenge type {ChallengeTypes.Http01}");
            }

            var keyAuth = httpChallenge.KeyAuthz;
            _challengeStore.AddChallengeResponse(httpChallenge.Token, keyAuth);

            _logger.LogTrace("Waiting for server to start accepting HTTP requests");
            await _appStarted.Task;

            _logger.LogTrace("Requesting server to validate HTTP challenge");
            await httpChallenge.Validate();
        }

        private async Task PrepareTlsAlpnChallengeResponseAsync(
            IAuthorizationContext authorizationContext,
            string domainName,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var tlsAlpnChallenge = await authorizationContext.TlsAlpn();

            _tlsAlpnChallengeResponder.PrepareChallengeCert(domainName, tlsAlpnChallenge.KeyAuthz);

            _logger.LogTrace("Waiting for server to start accepting HTTP requests");
            await _appStarted.Task;

            _logger.LogTrace("Requesting server to validate TLS/ALPN challenge");
            await tlsAlpnChallenge.Validate();
        }

        private Exception InvalidAuthorizationError(Authorization authorization)
        {
            var reason = "unknown";
            var domainName = authorization.Identifier.Value;
            try
            {
                var errors = authorization.Challenges.Where(a => a.Error != null).Select(a => a.Error)
                    .Select(error => $"{error.Type}: {error.Detail}, Code = {error.Status}");
                reason = string.Join("; ", errors);
            }
            catch
            {
                _logger.LogTrace("Could not determine reason why validation failed. Response: {resp}", authorization);
            }

            _logger.LogError("Failed to validate ownership of domainName '{domainName}'. Reason: {reason}", domainName,
                reason);

            return new InvalidOperationException($"Failed to validate ownership of domainName '{domainName}'");
        }

        private async Task<X509Certificate2> CompleteCertificateRequestAsync(IOrderContext order,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var commonName = _options.Value.DomainNames[0];
            _logger.LogDebug("Creating cert for {commonName}", commonName);

            var csrInfo = new CsrInfo
            {
                CommonName = commonName,
            };
            var privateKey = KeyFactory.NewKey((Certes.KeyAlgorithm) _options.Value.KeyAlgorithm);
            var acmeCert = await order.Generate(csrInfo, privateKey);

            _logger.LogAcmeAction("NewCertificate");

            var pfxBuilder = acmeCert.ToPfx(privateKey);
            var pfx = pfxBuilder.Build("HTTPS Cert - " + _options.Value.DomainNames, string.Empty);
            return new X509Certificate2(pfx, string.Empty, X509KeyStorageFlags.Exportable);
        }
    }
}
