export function readOnlyInput(root){
    if(root){
        let nodes = root.querySelectorAll('input');
        for(let node of nodes){
            if (node && !node.readOnly) node.readOnly = true;
        }
        if (document.activeElement) document.activeElement.blur();
    }
}
