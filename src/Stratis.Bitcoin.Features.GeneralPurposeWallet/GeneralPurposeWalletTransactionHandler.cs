﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Microsoft.Extensions.Logging;
using NBitcoin;
using NBitcoin.Policy;
using Stratis.Bitcoin.Features.GeneralPurposeWallet.Interfaces;
using Stratis.Bitcoin.Utilities;

namespace Stratis.Bitcoin.Features.GeneralPurposeWallet
{
    /// <summary>
    /// A handler that has various functionalities related to transaction operations.
    /// </summary>
    /// <remarks>
    /// This will uses the <see cref="IWalletFeePolicy"/> and the <see cref="TransactionBuilder"/>.
    /// TODO: Move also the broadcast transaction to this class
    /// TODO: Implement lockUnspents
    /// TODO: Implement subtractFeeFromOutputs
    /// </remarks>
    public class GeneralPurposeWalletTransactionHandler : IGeneralPurposeWalletTransactionHandler
    {
        /// <summary>A threshold that if possible will limit the amount of UTXO sent to the <see cref="ICoinSelector"/>.</summary>
        /// <remarks>
        /// 500 is a safe number that if reached ensures the coin selector will not take too long to complete,
        /// most regular wallets will never reach such a high number of UTXO.
        /// </remarks>
        private const int SendCountThresholdLimit = 500;

        private readonly IGeneralPurposeWalletManager walletManager;

        private readonly IGeneralPurposeWalletFeePolicy walletFeePolicy;

        private readonly CoinType coinType;

        private readonly ILogger logger;

        public GeneralPurposeWalletTransactionHandler(
            ILoggerFactory loggerFactory,
            IGeneralPurposeWalletManager walletManager,
            IGeneralPurposeWalletFeePolicy walletFeePolicy,
            Network network)
        {
            this.walletManager = walletManager;
            this.walletFeePolicy = walletFeePolicy;
            this.coinType = (CoinType)network.Consensus.CoinType;
            this.logger = loggerFactory.CreateLogger(this.GetType().FullName);
        }

        /// <inheritdoc />
        public Transaction BuildTransaction(TransactionBuildContext context)
        {
            this.InitializeTransactionBuilder(context);

            if (context.Shuffle)
            {
                context.TransactionBuilder.Shuffle();
            }

            // build transaction
            context.Transaction = context.TransactionBuilder.BuildTransaction(context.Sign);

			// If this is a multisig transaction, then by definition we only (usually) possess one of the keys
			// and can therefore not immediately construct a transaction that passes verification
	        if (!context.IgnoreVerify)
	        {
		        if (!context.TransactionBuilder.Verify(context.Transaction, out TransactionPolicyError[] errors))
		        {
			        string errorsMessage = string.Join(" - ", errors.Select(s => s.ToString()));
			        this.logger.LogError($"Build transaction failed: {errorsMessage}");
			        throw new GeneralPurposeWalletException($"Could not build the transaction. Details: {errorsMessage}");
		        }
	        }

	        return context.Transaction;
        }

        /// <inheritdoc />
        public void FundTransaction(TransactionBuildContext context, Transaction transaction)
        {
            if (context.Recipients.Any())
                throw new GeneralPurposeWalletException("Adding outputs is not allowed.");

            // Turn the txout set into a Recipient array
            context.Recipients.AddRange(transaction.Outputs
                .Select(s => new Recipient
                {
                    ScriptPubKey = s.ScriptPubKey,
                    Amount = s.Value,
                    SubtractFeeFromAmount = false // default for now
                }));

            context.AllowOtherInputs = true;

            foreach (var transactionInput in transaction.Inputs)
                context.SelectedInputs.Add(transactionInput.PrevOut);

            var newTransaction = this.BuildTransaction(context);

            if (context.ChangeAddress != null)
            {
                // find the position of the change and move it over.
                var index = 0;
                foreach (var newTransactionOutput in newTransaction.Outputs)
                {
                    if (newTransactionOutput.ScriptPubKey == context.ChangeAddress.ScriptPubKey)
                    {
                        transaction.Outputs.Insert(index, newTransactionOutput);
                    }

                    index++;
                }
            }

            // TODO: copy the new output amount size (this also includes spreading the fee over all outputs)

            // copy all the inputs from the new transaction.
            foreach (var newTransactionInput in newTransaction.Inputs)
            {
                if (!context.SelectedInputs.Contains(newTransactionInput.PrevOut))
                {
                    transaction.Inputs.Add(newTransactionInput);

                    // TODO: build a mechanism to lock inputs
                }
            }
        }

        /// <inheritdoc />
        public (Money maximumSpendableAmount, Money Fee) GetMaximumSpendableAmount(GeneralPurposeWalletAccountReference accountReference, FeeType feeType, bool allowUnconfirmed)
        {
            Guard.NotNull(accountReference, nameof(accountReference));
            Guard.NotEmpty(accountReference.WalletName, nameof(accountReference.WalletName));
            Guard.NotEmpty(accountReference.AccountName, nameof(accountReference.AccountName));

            // Get the total value of spendable coins in the account.
            var maxSpendableAmount = this.walletManager.GetSpendableTransactionsInAccount(accountReference, allowUnconfirmed ? 0 : 1).Sum(x => x.Transaction.Amount);

            // Return 0 if the user has nothing to spend.
            if (maxSpendableAmount == Money.Zero)
            {
                return (Money.Zero, Money.Zero);
            }

            // Create a recipient with a dummy destination address as it's required by NBitcoin's transaction builder.
            List<Recipient> recipients = new[] { new Recipient { Amount = new Money(maxSpendableAmount), ScriptPubKey = new Key().ScriptPubKey } }.ToList();
            Money fee;

            try
            {
                // Here we try to create a transaction that contains all the spendable coins, leaving no room for the fee.
                // When the transaction builder throws an exception informing us that we have insufficient funds,
                // we use the amount we're missing as the fee.
                var context = new TransactionBuildContext(accountReference, recipients, null)
                {
                    FeeType = feeType,
                    MinConfirmations = allowUnconfirmed ? 0 : 1,
                    TransactionBuilder = new TransactionBuilder()
                };

                this.AddRecipients(context);
                this.AddCoins(context);
                this.AddFee(context);

                // Throw an exception if this code is reached, as building a transaction without any funds for the fee should always throw an exception.
                throw new GeneralPurposeWalletException("This should be unreachable; please find and fix the bug that caused this to be reached.");
            }
            catch (NotEnoughFundsException e)
            {
                fee = (Money)e.Missing;
            }

            return (maxSpendableAmount - fee, fee);
        }

        /// <inheritdoc />
        public Money EstimateFee(TransactionBuildContext context)
        {
            this.InitializeTransactionBuilder(context);

            return context.TransactionFee;
        }

        /// <summary>
        /// Initializes the context transaction builder from information in <see cref="TransactionBuildContext"/>.
        /// </summary>
        /// <param name="context">Transaction build context.</param>
        private void InitializeTransactionBuilder(TransactionBuildContext context)
        {
            Guard.NotNull(context, nameof(context));
            Guard.NotNull(context.Recipients, nameof(context.Recipients));
            Guard.NotNull(context.AccountReference, nameof(context.AccountReference));

            context.TransactionBuilder = new TransactionBuilder();

            this.AddRecipients(context);
            this.AddCoins(context);
            this.AddSecrets(context);
            this.FindChangeAddress(context);
            this.AddFee(context);
        }

        /// <summary>
        /// Loads all the private keys for each <see cref="GeneralPurposeAddress"/> in <see cref="TransactionBuildContext.UnspentOutputs"/>
        /// </summary>
        /// <param name="context">The context associated with the current transaction being built.</param>
        private void AddSecrets(TransactionBuildContext context)
        {
            if (!context.Sign)
                return;

            GeneralPurposeWallet wallet = this.walletManager.GetWalletByName(context.AccountReference.WalletName);
	        var signingKeys = new HashSet<ISecret>();

			if (context.MultiSig == null)
	        {
		        var added = new HashSet<GeneralPurposeAddress>();

		        foreach (var unspentOutputsItem in context.UnspentOutputs)
		        {
			        if (added.Contains(unspentOutputsItem.Address))
				        continue;

			        var address = unspentOutputsItem.Address;
			        signingKeys.Add(address.PrivateKey.GetBitcoinSecret(wallet.Network));
			        added.Add(unspentOutputsItem.Address);
		        }
	        }
	        else
	        {
			    var added = new HashSet<MultiSigAddress>();

			    foreach (var unspentOutputsItem in context.UnspentMultiSigOutputs)
			    {
				    if (added.Contains(unspentOutputsItem.Address))
					    continue;

				    var address = unspentOutputsItem.Address;
				    signingKeys.Add(address.PrivateKey.GetBitcoinSecret(wallet.Network));
				    added.Add(unspentOutputsItem.Address);
			    }
			}

	        context.TransactionBuilder.AddKeys(signingKeys.ToArray());
        }

        /// <summary>
        /// Find the next available change address.
        /// </summary>
        /// <param name="context">The context associated with the current transaction being built.</param>
        private void FindChangeAddress(TransactionBuildContext context)
        {
	        if (context.MultiSig != null)
	        {
		        context.ChangeAddress = context.MultiSig.AsGeneralPurposeAddress();
		        context.TransactionBuilder.SetChange(context.MultiSig.ScriptPubKey);
			}
	        else
	        {
		        GeneralPurposeWallet wallet = this.walletManager.GetWalletByName(context.AccountReference.WalletName);
		        GeneralPurposeAccount account = wallet.AccountsRoot.Single(a => a.CoinType == this.coinType)
			        .GetAccountByName(context.AccountReference.AccountName);

		        // get address to send the change to
		        context.ChangeAddress = this.walletManager.GetOrCreateChangeAddress(account);
		        context.TransactionBuilder.SetChange(context.ChangeAddress.ScriptPubKey);
	        }
        }

		/// <summary>
		/// Find all available outputs (UTXO's) that belong to <see cref="GeneralPurposeWalletAccountReference.AccountName"/>.
		/// Then add them to the <see cref="TransactionBuildContext.UnspentOutputs"/> or <see cref="TransactionBuildContext.UnspentMultiSigOutputs"/>.
		/// </summary>
		/// <param name="context">The context associated with the current transaction being built.</param>
		private void AddCoins(TransactionBuildContext context)
        {
	        if (context.MultiSig == null)
	        {
		        context.UnspentOutputs = this.walletManager
			        .GetSpendableTransactionsInAccount(context.AccountReference, context.MinConfirmations).ToList();

				if (context.UnspentOutputs.Count == 0)
				{
					throw new GeneralPurposeWalletException("No spendable transactions found.");
				}

				// Get total spendable balance in the account.
				var balance = context.UnspentOutputs.Sum(t => t.Transaction.Amount);
				var totalToSend = context.Recipients.Sum(s => s.Amount);
				if (balance < totalToSend)
					throw new GeneralPurposeWalletException("Not enough funds.");

				if (context.SelectedInputs.Any())
				{
					// 'SelectedInputs' are inputs that must be included in the
					// current transaction. At this point we check the given
					// input is part of the UTXO set and filter out UTXOs that are not
					// in the initial list if 'context.AllowOtherInputs' is false.

					var availableHashList = context.UnspentOutputs.ToDictionary(item => item.ToOutPoint(), item => item);

					if (!context.SelectedInputs.All(input => availableHashList.ContainsKey(input)))
						throw new GeneralPurposeWalletException("Not all the selected inputs were found on the wallet.");

					if (!context.AllowOtherInputs)
					{
						foreach (var unspentOutputsItem in availableHashList)
							if (!context.SelectedInputs.Contains(unspentOutputsItem.Key))
								context.UnspentOutputs.Remove(unspentOutputsItem.Value);
					}
				}

				Money sum = 0;
				int index = 0;
				var coins = new List<Coin>();
				foreach (var item in context.UnspentOutputs.OrderByDescending(a => a.Transaction.Amount))
				{
					coins.Add(new Coin(item.Transaction.Id, (uint)item.Transaction.Index, item.Transaction.Amount, item.Transaction.ScriptPubKey));
					sum += item.Transaction.Amount;
					index++;

					// If threshold is reached and the total value is above the target
					// then its safe to stop adding UTXOs to the coin list.
					// The primary goal is to reduce the time it takes to build a trx
					// when the wallet is bloated with UTXOs.
					if (index > SendCountThresholdLimit && sum > totalToSend)
						break;
				}

		        // All the UTXOs are added to the builder without filtering.
		        // The builder then has its own coin selection mechanism
		        // to select the best UTXO set for the corresponding amount.
		        // To add a custom implementation of a coin selection override
		        // the builder using builder.SetCoinSelection().

		        context.TransactionBuilder.AddCoins(coins);
			}
	        else
	        {
		        context.UnspentMultiSigOutputs = this.walletManager
			        .GetSpendableMultiSigTransactionsInAccount(context.AccountReference, context.MultiSig.ScriptPubKey,
				        context.MinConfirmations).ToList();

		        if (context.UnspentMultiSigOutputs.Count == 0)
		        {
			        throw new GeneralPurposeWalletException("No spendable transactions found.");
		        }

		        // Get total spendable balance in the account.
		        var balance = context.UnspentMultiSigOutputs.Sum(t => t.Transaction.Amount);
		        var totalToSend = context.Recipients.Sum(s => s.Amount);
		        if (balance < totalToSend)
			        throw new GeneralPurposeWalletException("Not enough funds.");

		        if (context.SelectedInputs.Any())
		        {
			        // 'SelectedInputs' are inputs that must be included in the
			        // current transaction. At this point we check the given
			        // input is part of the UTXO set and filter out UTXOs that are not
			        // in the initial list if 'context.AllowOtherInputs' is false.

			        var availableHashList = context.UnspentMultiSigOutputs.ToDictionary(item => item.ToOutPoint(), item => item);

			        if (!context.SelectedInputs.All(input => availableHashList.ContainsKey(input)))
				        throw new GeneralPurposeWalletException("Not all the selected inputs were found on the wallet.");

			        if (!context.AllowOtherInputs)
			        {
				        foreach (var unspentOutputsItem in availableHashList)
					        if (!context.SelectedInputs.Contains(unspentOutputsItem.Key))
						        context.UnspentMultiSigOutputs.Remove(unspentOutputsItem.Value);
			        }
		        }

		        Money sum = 0;
		        int index = 0;
		        var coins = new List<Coin>();
		        foreach (var item in context.UnspentMultiSigOutputs.OrderByDescending(a => a.Transaction.Amount))
		        {
					coins.Add(new ScriptCoin(item.Transaction.Id, (uint)item.Transaction.Index, item.Transaction.Amount, item.Transaction.ScriptPubKey, item.Address.RedeemScript));
			        sum += item.Transaction.Amount;
			        index++;

			        // If threshold is reached and the total value is above the target
			        // then its safe to stop adding UTXOs to the coin list.
			        // The primary goal is to reduce the time it takes to build a trx
			        // when the wallet is bloated with UTXOs.
			        if (index > SendCountThresholdLimit && sum > totalToSend)
				        break;
		        }

		        // All the UTXOs are added to the builder without filtering.
		        // The builder then has its own coin selection mechanism
		        // to select the best UTXO set for the corresponding amount.
		        // To add a custom implementation of a coin selection override
		        // the builder using builder.SetCoinSelection().

		        context.TransactionBuilder.AddCoins(coins);
			}
        }

        /// <summary>
        /// Add recipients to the <see cref="TransactionBuilder"/>.
        /// </summary>
        /// <param name="context">The context associated with the current transaction being built.</param>
        /// <remarks>
        /// Add outputs to the <see cref="TransactionBuilder"/> based on the <see cref="Recipient"/> list.
        /// </remarks>
        private void AddRecipients(TransactionBuildContext context)
        {
            if (context.Recipients.Any(a => a.Amount == Money.Zero))
                throw new GeneralPurposeWalletException("No amount specified.");

            if (context.Recipients.Any(a => a.SubtractFeeFromAmount))
                throw new NotImplementedException("Substracting the fee from the recipient is not supported yet.");

            foreach (var recipient in context.Recipients)
                context.TransactionBuilder.Send(recipient.ScriptPubKey, recipient.Amount);
        }

        /// <summary>
        /// Use the <see cref="FeeRate"/> from the <see cref="walletFeePolicy"/>.
        /// </summary>
        /// <param name="context">The context associated with the current transaction being built.</param>
        private void AddFee(TransactionBuildContext context)
        {
            Money fee;

            // If the fee hasn't been set manually, calculate it based on the fee type that was chosen.
            if (context.TransactionFee == null)
            {
                FeeRate feeRate = context.OverrideFeeRate ?? this.walletFeePolicy.GetFeeRate(context.FeeType.ToConfirmations());
                fee = context.TransactionBuilder.EstimateFees(feeRate);
            }
            else
            {
                fee = context.TransactionFee;
            }

            context.TransactionBuilder.SendFees(fee);
            context.TransactionFee = fee;
        }
    }

    public class TransactionBuildContext
    {
        /// <summary>
        /// Initialize a new instance of a <see cref="TransactionBuildContext"/>
        /// </summary>
        /// <param name="accountReference">The wallet and account from which to build this transaction</param>
        /// <param name="recipients">The target recipients to send coins to.</param>
        public TransactionBuildContext(GeneralPurposeWalletAccountReference accountReference, List<Recipient> recipients)
            : this(accountReference, recipients, string.Empty)
        {
        }

        /// <summary>
        /// Initialize a new instance of a <see cref="TransactionBuildContext"/>
        /// </summary>
        /// <param name="accountReference">The wallet and account from which to build this transaction</param>
        /// <param name="recipients">The target recipients to send coins to.</param>
        /// <param name="walletPassword">The password that protects the wallet in <see cref="accountReference"/></param>
        public TransactionBuildContext(GeneralPurposeWalletAccountReference accountReference, List<Recipient> recipients, string walletPassword)
        {
            Guard.NotNull(recipients, nameof(recipients));

            this.AccountReference = accountReference;
            this.Recipients = recipients;
            this.WalletPassword = walletPassword;
            this.FeeType = FeeType.Medium;
            this.MinConfirmations = 1;
            this.SelectedInputs = new List<OutPoint>();
            this.AllowOtherInputs = false;
            this.Sign = !string.IsNullOrEmpty(walletPassword);
	        this.MultiSig = null;
	        this.IgnoreVerify = false;
        }

        /// <summary>
        /// The wallet account to use for building a transaction
        /// </summary>
        public GeneralPurposeWalletAccountReference AccountReference { get; set; }

        /// <summary>
        /// The recipients to send Bitcoin to.
        /// </summary>
        public List<Recipient> Recipients { get; set; }

        /// <summary>
        /// An indicator to estimate how much fee to spend on a transaction.
        /// </summary>
        /// <remarks>
        /// The higher the fee the faster a transaction will get in to a block.
        /// </remarks>
        public FeeType FeeType { get; set; }

        /// <summary>
        /// The minimum number of confirmations an output must have to be included as an input.
        /// </summary>
        public int MinConfirmations { get; set; }

        /// <summary>
        /// Coins that are available to be spent.
        /// </summary>
        public List<UnspentOutputReference> UnspentOutputs { get; set; }

	    /// <summary>
	    /// Coins that are available to be spent from the selected multisig address.
	    /// </summary>
	    public List<UnspentMultiSigOutputReference> UnspentMultiSigOutputs { get; set; }

		public Network Network { get; set; }

		/// <summary>
		/// The builder used to build the current transaction.
		/// </summary>
		public TransactionBuilder TransactionBuilder { get; set; }

        /// <summary>
        /// The change address, where any remaining funds will be sent to.
        /// </summary>
        /// <remarks>
        /// A Bitcoin has to spend the entire UTXO, if total value is greater then the send target
        /// the rest of the coins go in to a change address that is under the senders control.
        /// </remarks>
        public GeneralPurposeAddress ChangeAddress { get; set; }

        /// <summary>
        /// The total fee on the transaction.
        /// </summary>
        public Money TransactionFee { get; set; }

        /// <summary>
        /// The final transaction.
        /// </summary>
        public Transaction Transaction { get; set; }

        /// <summary>
        /// The password that protects the wallet in <see cref="WalletAccountReference"/>.
        /// </summary>
        /// <remarks>
        /// TODO: replace this with System.Security.SecureString (https://github.com/dotnet/corefx/tree/master/src/System.Security.SecureString)
        /// More info (https://github.com/dotnet/corefx/issues/1387)
        /// </remarks>
        public string WalletPassword { get; set; }

        /// <summary>
        /// The inputs that must be used when building the transaction.
        /// </summary>
        /// <remarks>
        /// The inputs are required to be part of the wallet.
        /// </remarks>
        public List<OutPoint> SelectedInputs { get; set; }

        /// <summary>
        /// If false, allows unselected inputs, but requires all selected inputs be used
        /// </summary>
        public bool AllowOtherInputs { get; set; }

        /// <summary>
        /// Specify whether to sign the transaction.
        /// </summary>
        public bool Sign { get; set; }

        /// <summary>
        /// Allows the context to specify a <see cref="FeeRate"/> when building a transaction.
        /// </summary>
        public FeeRate OverrideFeeRate { get; set; }

        /// <summary>
        /// Shuffles transaction inputs and outputs for increased privacy.
        /// </summary>
        public bool Shuffle { get; set; }

	    /// <summary>
	    /// If not null, indicates the multisig address details that funds can be sourced from.
	    /// </summary>
	    public MultiSigAddress MultiSig { get; set; }

	    /// <summary>
	    /// If true, do not perform verification on the built transaction (e.g. it is partially signed)
	    /// </summary>
	    public bool IgnoreVerify { get; set; }
	}

    /// <summary>
    /// Represents recipients of a payment, used in <see cref="GeneralPurposeWalletTransactionHandler.BuildTransaction"/>
    /// </summary>
    public class Recipient
    {
        /// <summary>
        /// The destination script.
        /// </summary>
        public Script ScriptPubKey { get; set; }

        /// <summary>
        /// The amount that will be sent.
        /// </summary>
        public Money Amount { get; set; }

        /// <summary>
        /// An indicator if the fee is subtracted from the current recipient.
        /// </summary>
        public bool SubtractFeeFromAmount { get; set; }
    }
}