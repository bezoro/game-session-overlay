using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ApolloOverlay;

public partial class MainWindow
{
    private void OpenKeyboard(bool forProfile)
    {
        keyboardForProfile=forProfile;keyboardText=forProfile?profileDraft?.Name??"":"";keyboardUpper=false;keyboardSymbols=false;
        keyboardTarget=Controller.ReturnTarget is {} previous&&Controller.Platform.Valid(previous)&&!previous.Protected?previous:Controller.Session.Anchor;
        OpenFeature(OverlayRoute.Keyboard,"key:q");
    }
    private void BuildSessionKeyboard()
    {
        Paragraph(keyboardForProfile?"Nome do perfil":"Posicione o cursor no campo desejado antes de abrir o painel. Inserir texto não pressiona Enter.");
        if(!keyboardForProfile)AddRow("keyboard-target",keyboardTarget?.Title??"Nenhuma janela selecionada","Destino do texto · A troca a janela",()=>
        {
            var targets=Controller.Apps.Where(target=>!target.Protected&&Controller.Platform.Valid(target)).ToArray();
            var index=Array.FindIndex(targets,target=>target.Same(keyboardTarget));keyboardTarget=targets.Length>0?targets[(index+1)%targets.Length]:null;RefreshRows();
        });
        var editorButton=AddRow("keyboard-editor","Seu texto",keyboardText.Length+" / "+(keyboardForProfile?48:512)+" caracteres",()=>keyboardEditor?.Focus());
        keyboardEditor=new TextBox{Text=keyboardText,MaxLength=keyboardForProfile?48:512,AcceptsReturn=false,TextWrapping=TextWrapping.Wrap,MinHeight=64,Margin=new(0,8,0,0),FontSize=20};
        keyboardEditor.SetResourceReference(ForegroundProperty,ThemeSupport.Text);keyboardEditor.SetResourceReference(BackgroundProperty,ThemeSupport.Surface);
        keyboardEditor.SetResourceReference(BorderBrushProperty,ThemeSupport.Stroke);keyboardEditor.SetResourceReference(TextBox.CaretBrushProperty,ThemeSupport.Focus);keyboardEditor.SetResourceReference(TextBox.SelectionBrushProperty,ThemeSupport.FocusBackground);
        keyboardEditor.Padding=new(10);keyboardEditor.BorderThickness=new(1);
        var editor=keyboardEditor;editor.TextChanged+=(_,_)=>{keyboardText=editor.Text;SetEntryDescription("keyboard-editor",keyboardText.Length+" caracteres");};
        ((StackPanel)((Grid)editorButton.Content).Children[1]).Children.Add(editor);
        var characters=keyboardSymbols?"1234567890@#$%&*()-_áéíóúãõâêô":"qwertyuiopasdfghjklçzxcvbnm,.?";
        var columns=narrow||Rows.ActualWidth<480?8:10;var board=new Grid{Margin=new(0,10,0,10)};
        for(var column=0;column<columns;column++)board.ColumnDefinitions.Add(new());
        for(var row=0;row<(characters.Length+columns-1)/columns;row++)board.RowDefinitions.Add(new(){Height=GridLength.Auto});
        for(var index=0;index<characters.Length;index++)
        {
            var character=characters[index].ToString();var value=keyboardUpper?character.ToUpperInvariant():character;
            var button=new Button{Content=value,Tag="key:"+character,Padding=new(0),Margin=new(2),MinHeight=44,FontSize=19,HorizontalContentAlignment=HorizontalAlignment.Center};
            var id="key:"+character;button.Click+=(_,_)=>ActivatePointerEntry(button);button.GotKeyboardFocus+=(_,_)=>{navigation=new(id);SetSelection(false,false);};
            System.Windows.Automation.AutomationProperties.SetName(button,"Inserir "+value);Grid.SetRow(button,index/columns);Grid.SetColumn(button,index%columns);board.Children.Add(button);
            RegisterFixed(id,button,()=>AppendKeyboard(value),true,"Inserir "+value);
        }
        var boardRow=Rows.RowDefinitions.Count;Rows.RowDefinitions.Add(new(){Height=GridLength.Auto});Grid.SetRow(board,boardRow);Grid.SetColumnSpan(board,2);Rows.Children.Add(board);
        var first=Rows.RowDefinitions.Count;
        AddRow("keyboard-space","Espaço","Inserir um espaço",()=>AppendKeyboard(" "),row:narrow?null:first,column:0);
        AddRow("keyboard-delete","Apagar","Remover o último caractere",()=>{keyboardText=SessionTextEntry.Backspace(keyboardText);if(keyboardEditor is not null)keyboardEditor.Text=keyboardText;},row:narrow?null:first,column:1);
        first=Rows.RowDefinitions.Count;
        AddRow("keyboard-case",keyboardUpper?"Minúsculas":"Maiúsculas","Alternar letras",()=>{keyboardUpper=!keyboardUpper;RefreshRows();},row:narrow?null:first,column:0);
        AddRow("keyboard-symbols",keyboardSymbols?"ABC":"123 e acentos","Trocar o conjunto de teclas",()=>{keyboardSymbols=!keyboardSymbols;RefreshRows();},row:narrow?null:first,column:1);
        AddRow("keyboard-clear","Limpar texto","Apagar o texto em edição",()=>{keyboardText="";if(keyboardEditor is not null)keyboardEditor.Text="";});
        AddRow("keyboard-insert",keyboardForProfile?"Usar este nome":"Inserir na janela",keyboardForProfile?"Voltar ao perfil sem enviá-lo a outro aplicativo":"Envia somente o texto; confirme o campo no aplicativo",SubmitSessionKeyboard,keyboardForProfile||keyboardTarget is not null);
        AddRow("back","Voltar","Não enviar o texto",Back);
    }
    private void AppendKeyboard(string value)
    {
        var maximum=keyboardForProfile?48:512;if(keyboardText.Length+value.Length>maximum){FeedbackText("Limite de texto atingido.");return;}
        keyboardText+=value;if(keyboardEditor is not null)keyboardEditor.Text=keyboardText;
    }
    private void SubmitSessionKeyboard()
    {
        if(keyboardForProfile)
        {
            var name=keyboardText.Trim();if(name.Length==0){FeedbackText("Escolha um nome para o perfil.");return;}
            if(profileDraft is not null)profileDraft=profileDraft with{Name=name};Back();return;
        }
        if(keyboardTarget is not {} target||keyboardText.Length==0){FeedbackText("Escolha a janela e escreva o texto antes de inserir.");return;}
        var text=keyboardText;
        if(diagnostic||Controller.Platform.IsPreview){RecordResult(new(ResultKind.Simulated,"Prévia: nenhum texto foi enviado a outro aplicativo."),"keyboard");return;}
        var writer=new SessionTextEntry(new WindowsTextEntryEffects(Controller.Platform));
        RunEffect((token,keep)=>writer.InsertAsync(target,text,keep,token),()=>Controller.Platform.Valid(target),target);
    }
    private bool HandleSessionKeyboardKey(KeyEventArgs args)
    {
        if(view.Current.Route!=OverlayRoute.Keyboard||keyboardEditor?.IsKeyboardFocusWithin!=true||args.Key!=Key.Tab)return false;
        if(NavigationAllowed&&navigationKeysArmed)Sequential(KeyboardModifiersReader().HasFlag(ModifierKeys.Shift)?-1:1);
        args.Handled=true;return true;
    }
}
